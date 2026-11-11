using System.Text;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Providers.LibraryModels.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class SourceCodeStatisticsTests
{
    private const string Source = "public static class Api { public static void Run(System.Action action) => action(); }";
    private const string Optional = "public interface IMarker { }";

    [Theory]
    [InlineData("reference", GenerationReasons.REFERENCE_ASSEMBLY)]
    [InlineData("missing", GenerationReasons.NO_IMPLEMENTATION)]
    [InlineData("implementation", null)]
    public void Code_counts_require_successful_implementation_resolution(string implementation, string? reason)
    {
        using var install = new GenerationInstall();
        var image = EmittedAssemblies.Image("Library", Source, referenceAssembly: implementation == "reference");
        var directory = install.SharedFramework("8.0.5", runtime: true,
            images: new Dictionary<string, byte[]> { ["Library"] = image });
        var path = Path.Combine(directory, "Library.dll");
        var compilation = CSharpCompilation.Create("App", references:
            [.. EmittedAssemblies.RuntimeReferences, MetadataReference.CreateFromImage(image, filePath: path)]);
        var member = compilation.GetTypeByMetadataName("Api")!.GetMembers("Run").OfType<IMethodSymbol>().Single();
        var resolver = install.Resolver();
        var target = Assert.IsType<GenerationTarget>(GenerationTargets.Of(member, compilation, resolver,
            CancellationToken.None, generationFrameworkMajor: 8));
        var candidate = new GenerationCandidate(compilation, member, target, true);
        if (implementation == "missing")
            File.Delete(path);
        using var sources = new LibrarySources(true, Path.Combine(install.Root, "sources"));
        var prepared = ModelGenerator.Prepare(target.Request, resolver, sources, CancellationToken.None)!;
        Assert.Equal(reason, prepared.Resolution.Reason);
        Assert.Equal(implementation != "missing", prepared.Resolution.Assembly is not null);
        Assert.Equal(reason is null, prepared.Sources is not null);
        var ownership = new CompilationOwnership();
        GeneratedAnswer answer;
        try
        {
            using (ownership.Open())
                answer = ModelGenerator.TraceWithin(target.Request, resolver, prepared: prepared).Answer;
        }
        finally { LibraryCompilation.Release(ownership.Created); }
        Assert.Equal(reason, answer.Reason);
        Assert.Equal(reason is null, answer.Generation.Code is not null);
        if (reason is null)
            Assert.Equal(LibraryCodeKind.DecompiledBest, answer.Generation.Code!.Kind);
        else
            Assert.Null(prepared.Code);

        using var pass = GenerationPass.Create(new GenerationSetup(ModelGeneration.Generate, resolver, Sources: sources),
            Path.Combine(install.Root, "repository"), CancellationToken.None)!;
        var layer = pass.Prepare(LibraryModels.BuiltIn, [candidate]);
        var generated = Assert.Single(pass.Reached(LibraryModels.BuiltIn, layer, [candidate])).Standing.Generated;
        Assert.NotNull(generated);
        Assert.Equal(reason, generated.Refusal?.Reason);
        Assert.Equal(0, sources.Statistics.Libraries.OriginalSources);
        Assert.Equal(reason is null ? 1 : 0, sources.Statistics.Libraries.DecompiledBest);
        Assert.Equal(0, sources.Statistics.Libraries.DecompiledForRun);
        Assert.Equal(0, sources.Statistics.Downloads.Requests);
    }

    public static IEnumerable<object[]> Cuts()
    {
        foreach (var absence in new[] { "none", "persistent", "transient", "offline" })
        foreach (var cached in new[] { false, true })
        foreach (var point in new[] { "not-started", "compiling", "succeeded", "fallback" })
        foreach (var outcome in new[] { "normal", "timeout", "budget", "failed" })
            if (outcome != "normal" || point is "succeeded" or "fallback")
                yield return [absence, cached, point, outcome];
    }

    [Theory]
    [MemberData(nameof(Cuts))]
    public async Task Metrics_count_observed_code_for_normal_and_cut_answers(string absence, bool cached, string point, string outcome)
    {
        await using var server = new LoopbackSymbolServer();
        using var fixture = new LibrarySourceCompilationTests.Fixture(Source, Optional, sourceServer: server.Address);
        server.Replies["/sources/AssemblyInfo.cs"] = absence == "none" ? new(Bytes: Encoding.UTF8.GetBytes(Optional))
            : new(absence == "transient" ? 503 : 404);
        using var sources = new LibrarySources(absence == "offline", Path.Combine(fixture.Install.Root, "sources"), httpsOnly: false);
        var resolver = fixture.Install.Resolver();
        var prepared = ModelGenerator.Prepare(fixture.Request, resolver, sources, CancellationToken.None)!;
        Assert.Equal(absence == "none" ? 0 : 1, prepared.Sources!.MissingMethodlessDocuments.Count);
        if (point == "fallback")
            prepared = prepared with { Sources = prepared.Sources with
            {
                Documents = prepared.Sources.Documents.Select(document => document.Path == "/src/library.cs"
                    ? new LibrarySourceDocument(document.Path, [.. Encoding.UTF8.GetBytes("public class Broken : MissingType { }")]) : document).ToArray()
            } };
        if (cached)
            fixture.Compile(prepared.Sources);
        var compilation = CSharpCompilation.Create("App", references:
            [.. EmittedAssemblies.RuntimeReferences, MetadataReference.CreateFromFile(fixture.Assembly.Path)]);
        var member = compilation.GetTypeByMetadataName("Api")!.GetMembers("Run").OfType<IMethodSymbol>().Single();
        var target = Assert.IsType<GenerationTarget>(GenerationTargets.Of(member, compilation, resolver,
            CancellationToken.None, generationFrameworkMajor: 8));
        var candidate = new GenerationCandidate(compilation, member, target, true);
        var clock = new FakeClock();
        GeneratedAnswer? generated = null;
        var reached = false;
        var subject = new RunGeneration(Path.Combine(fixture.Install.Root, "repository"), CancellationToken.None,
            outcome == "budget" ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(2), clock,
            (_, _, _) => throw new InvalidOperationException("must use preparation"), (_, _) => 0, workers: 1,
            prepare: (_, _) => (() => prepared.RunOnly, (_, token) =>
            {
                void Cut()
                {
                    reached = true;
                    if (outcome == "normal") return;
                    if (outcome == "failed") throw new InvalidOperationException("test cut");
                    clock.Advance(TimeSpan.FromSeconds(3));
                    token.ThrowIfCancellationRequested();
                }
                var started = 0;
                var waiting = 0;
                var stopAt = point == "fallback" ? 2 : 1;
                var probe = new LibraryCompilationProbe
                {
                    Started = () =>
                    {
                        if (!cached && outcome != "failed" && Interlocked.Increment(ref started) == stopAt && point is "compiling" or "fallback") Cut();
                    },
                    Waiting = () =>
                    {
                        if ((cached || outcome == "failed") && ++waiting == stopAt && point is "compiling" or "fallback") Cut();
                    }
                };
                try
                {
                    return generated = ModelGenerator.TraceWithin(fixture.Request, resolver, TimeSpan.FromMinutes(1), cancellationToken: token,
                        checkReported: (stage, _) =>
                        {
                            if (point == "not-started" && stage == GenerationStage.Resolve || point == "succeeded" && stage == GenerationStage.EngineClaims) Cut();
                        }, compilationProbe: probe, prepared: prepared).Answer;
                }
                finally { prepared.RecordObservedCode(sources); }
            }));

        try { subject.Generate(LibraryModels.BuiltIn, [candidate]); }
        finally
        {
            // A synchronous test failure at Waiting leaves a healthy compilation running; let it finish before removing its files.
            try { if (outcome == "failed" && point is "compiling" or "fallback") fixture.Compile(prepared.Sources); }
            finally { subject.Release(); }
        }

        Assert.True(reached);
        var expected = point == "fallback" ? LibrarySourceDecisions.ForCompilation(prepared.Sources, sourcesRejected: true)
            : LibrarySourceDecisions.ForCompilation(prepared.Sources);
        AssertCounts(sources, expected.Kind);
        Assert.Equal(expected, prepared.ObservedCode);
        Assert.Equal(point is "succeeded" or "fallback", prepared.Code is not null);
        var answer = subject.AnswerOf(target)!;
        Assert.Equal(outcome switch
        {
            "timeout" => GenerationOutcomeKind.Timeout,
            "budget" => GenerationOutcomeKind.Budget,
            "failed" => GenerationOutcomeKind.Failed,
            _ => GenerationOutcomeKind.Model
        }, answer.Outcome.Kind);
        if (generated is not null)
            Assert.Equal(expected, generated.Generation.Code);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("persistent", false)]
    [InlineData("persistent", true)]
    [InlineData("transient", false)]
    [InlineData("transient", true)]
    [InlineData("offline", false)]
    [InlineData("offline", true)]
    public async Task Timeout_record_and_metrics_keep_sources_while_the_compile_outcome_is_unknown(string absence, bool cached)
    {
        await using var server = new LoopbackSymbolServer();
        using var fixture = new LibrarySourceCompilationTests.Fixture(Source, Optional, sourceServer: server.Address);
        server.Replies["/sources/AssemblyInfo.cs"] = absence == "none" ? new(Bytes: Encoding.UTF8.GetBytes(Optional))
            : new(absence == "transient" ? 503 : 404);
        using var sources = new LibrarySources(absence == "offline", Path.Combine(fixture.Install.Root, "sources"), httpsOnly: false);
        var resolver = fixture.Install.Resolver();
        var prepared = ModelGenerator.Prepare(fixture.Request, resolver, sources, CancellationToken.None)!;
        if (cached)
            fixture.Compile(prepared.Sources);
        var clock = new FakeClock();
        void Cut()
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            WorkCancellation.Check();
        }
        var probe = new LibraryCompilationProbe
        {
            Started = () => { if (!cached) Cut(); },
            Waiting = () => { if (cached) Cut(); }
        };
        GeneratedAnswer answer;
        using (fixture.Ownership.Open())
        {
            try
            {
                answer = ModelGenerator.TraceWithin(fixture.Request, resolver, TimeSpan.FromSeconds(1), clock,
                    compilationProbe: probe, prepared: prepared).Answer;
            }
            finally { prepared.RecordObservedCode(sources); }
        }
        Assert.Equal(GenerationReasons.TIMEOUT, answer.Reason);
        Assert.Null(prepared.Code);
        Assert.Equal(LibraryCodeKind.OriginalSources, answer.Generation.Code!.Kind);
        Assert.Null(answer.Generation.Code.Reason);
        Assert.Equal(answer.Generation.Code, prepared.ObservedCode);
        AssertCounts(sources, LibraryCodeKind.OriginalSources);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SourceOutcome.Persistent)]
    [InlineData(SourceOutcome.Transient)]
    [InlineData(SourceOutcome.UnavailableOffline)]
    public void Conservative_finality_is_separate_from_observed_code(SourceOutcome? absence)
    {
        var prepared = new GenerationPreparation(null!, new LibrarySourceResult(LibraryCodeKind.OriginalSources, null, [], [],
            LanguageVersion.Default, "fixture") { MissingMethodlessDocuments = absence is { } missing ? [missing] : [] });
        var runOnlyIfCut = absence is SourceOutcome.Transient or SourceOutcome.UnavailableOffline;
        Assert.Equal(runOnlyIfCut, prepared.RunOnly);
        Assert.Equal(LibraryCodeKind.OriginalSources, prepared.ObservedCode.Kind);
        Assert.Null(prepared.ObservedCode.Reason);
        prepared.Code = LibrarySourceDecisions.ForCompilation(prepared.Sources);
        Assert.False(prepared.RunOnly);
        Assert.Equal(LibraryCodeKind.OriginalSources, prepared.ObservedCode.Kind);
        prepared.Code = LibrarySourceDecisions.ForCompilation(prepared.Sources, sourcesRejected: true);
        Assert.Equal(runOnlyIfCut, prepared.RunOnly);
        Assert.Equal(runOnlyIfCut ? LibraryCodeKind.DecompiledForRun : LibraryCodeKind.DecompiledBest, prepared.ObservedCode.Kind);
    }

    private static void AssertCounts(LibrarySources sources, LibraryCodeKind kind)
    {
        Assert.Equal(kind == LibraryCodeKind.OriginalSources ? 1 : 0, sources.Statistics.Libraries.OriginalSources);
        Assert.Equal(kind == LibraryCodeKind.DecompiledBest ? 1 : 0, sources.Statistics.Libraries.DecompiledBest);
        Assert.Equal(kind == LibraryCodeKind.DecompiledForRun ? 1 : 0, sources.Statistics.Libraries.DecompiledForRun);
    }
}
