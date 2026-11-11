using System.Text.Json;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Providers.LibraryModels.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using GenerationCauses = ConcurrencyHunter.Analysis.GenerationCauses;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class RunOnlyAnswerTests : IDisposable
{
    private const string PACKAGE = "RunOnly.Library";
    private readonly GenerationInstall _install = new();
    private readonly FakeClock _clock = new();
    private readonly GenerationCandidate[] _candidates;
    private string Repository => Path.Combine(_install.Root, "repository");

    public RunOnlyAnswerTests()
    {
        _install.SharedFramework("8.0.5");
        var image = EmittedAssemblies.Image(PACKAGE, ReferenceScopes.VERSION_ATTRIBUTE + """
            public static class Api
            {
                public static void Alpha(object value) { }
                public static void Beta(object value) { }
                public static void Gamma(object value) { }
                public static void Delta(object value) { }
            }
            """);
        var path = ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, image);
        var compilation = ReferenceScopes.Compile("public class App { }",
            [.. ReferenceScopes.Runtime(_install, 8), MetadataReference.CreateFromFile(path)]);
        _candidates = new[] { "Alpha", "Beta", "Gamma", "Delta" }.Select(name =>
        {
            var member = compilation.GetTypeByMetadataName("Api")!.GetMembers(name).OfType<IMethodSymbol>().Single();
            var target = Assert.IsType<GenerationTarget>(GenerationTargets.Of(member, compilation, _install.Resolver(), CancellationToken.None));
            return new GenerationCandidate(compilation, member, target, false);
        }).ToArray();
    }

    [Fact]
    public void Run_only_model_applies_to_dependencies_and_scope_but_neither_it_nor_its_readers_are_stored()
    {
        var subject = Subject((request, answers, _) =>
        {
            var answer = Model(request);
            if (request.MemberId == _candidates[1].Target.MemberId)
                return answer with { DependencyViews = [Read(answers, _candidates[0])] };
            if (request.MemberId == _candidates[2].Target.MemberId)
                return answer with { DependencyViews = [Read(answers, _candidates[1])] };
            return answer;
        });

        subject.Generate(LibraryModels.BuiltIn, _candidates);
        subject.EndScope();

        var layer = subject.Layer(LibraryModels.BuiltIn, _candidates);
        Assert.True(layer.Describes);
        Assert.Empty(layer.Diagnostics);
        foreach (var candidate in _candidates)
            Assert.Equal(StoredGeneratedKind.Model, Lookup(layer, candidate).Kind);
        Assert.False(subject.HasWork);
        // Delta is independent and shares the same file: the file may keep it without keeping the other three.
        Assert.Equal([_candidates[3].Target.MemberId], Stored(_candidates[0]).Entries.Keys);
        using var json = JsonDocument.Parse(File.ReadAllText(_candidates[0].Target.Key.PathUnder(Repository)!));
        Assert.Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(["member", "model"], json.RootElement.GetProperty("members")[0].EnumerateObject().Select(property => property.Name));
        Assert.Null(GenerationCauses.Of(ModelGeneration.Generate,
            new TargetStanding(Lookup(layer, _candidates[0]), false, subject.AnswerOf(_candidates[0].Target), true)));
    }

    [Fact]
    public void Run_only_refusal_is_visible_with_its_coverage_reason_and_prevents_a_readers_finality()
    {
        var subject = Subject((request, answers, _) => request.MemberId == _candidates[0].Target.MemberId
            ? Refusal(request)
            : Model(request) with { DependencyViews = [Read(answers, _candidates[0], refusal: true)] });

        subject.Generate(LibraryModels.BuiltIn, _candidates[..2]);
        subject.EndScope();

        var layer = subject.Layer(LibraryModels.BuiltIn, _candidates[..2]);
        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, Lookup(layer, _candidates[0]).Refusal!.Word);
        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, GenerationCauses.Of(ModelGeneration.Generate,
            new TargetStanding(Lookup(layer, _candidates[0]), false, subject.AnswerOf(_candidates[0].Target))));
        Assert.Equal(StoredGeneratedKind.Model, Lookup(layer, _candidates[1]).Kind);
        Assert.Empty(Stored(_candidates[0]).Entries);
        Assert.False(subject.HasWork);
    }

    [Fact]
    public void Synthetic_timeout_of_run_only_code_and_the_answer_that_saw_it_are_not_stored()
    {
        var subject = Subject((request, answers, token) => request.MemberId == _candidates[0].Target.MemberId
            ? UntilCut(token)
            : Model(request) with { DependencyViews = [Read(answers, _candidates[0], refusal: true)] });

        subject.Generate(LibraryModels.BuiltIn, _candidates[..2]);
        subject.EndScope();

        Assert.Equal(GenerationOutcomeKind.Timeout, subject.AnswerOf(_candidates[0].Target)!.Outcome.Kind);
        Assert.Equal(GenerationOutcomeKind.Model, subject.AnswerOf(_candidates[1].Target)!.Outcome.Kind);
        Assert.Equal(GenerationOutcome.TIMEOUT, Lookup(subject.Layer(LibraryModels.BuiltIn, _candidates), _candidates[0]).Refusal!.Word);
        Assert.Empty(Stored(_candidates[0]).Entries);
        Assert.False(subject.HasWork);
    }

    [Fact]
    public void Synthetic_timeouts_on_best_code_are_stored_even_after_reading_a_run_only_answer()
    {
        var readRunOnly = false;
        var subject = Subject((request, answers, token) =>
        {
            if (request.MemberId == _candidates[0].Target.MemberId)
                return Model(request);
            if (request.MemberId == _candidates[1].Target.MemberId)
            {
                Read(answers, _candidates[0]);
                readRunOnly = true;
                return UntilCut(token);
            }
            if (request.MemberId == _candidates[2].Target.MemberId)
                return Model(request) with { DependencyViews = [Read(answers, _candidates[1], refusal: true)] };
            return UntilCut(token);
        });

        subject.Generate(LibraryModels.BuiltIn, _candidates);
        subject.EndScope();

        Assert.True(readRunOnly);
        var stored = Stored(_candidates[0]);
        Assert.False(stored.Entries.ContainsKey(_candidates[0].Target.MemberId));
        Assert.Equal(3, stored.Entries.Count);
        Assert.Equal(GenerationOutcome.TIMEOUT, stored.Entries[_candidates[1].Target.MemberId].Refusal!.Word);
        Assert.NotNull(stored.Entries[_candidates[2].Target.MemberId].Model);
        Assert.Equal(GenerationOutcome.TIMEOUT, stored.Entries[_candidates[3].Target.MemberId].Refusal!.Word);
        Assert.False(subject.HasWork);
    }

    [Fact]
    public void Budget_cut_after_preparation_keeps_the_run_only_answer_in_memory()
    {
        var prepared = 0;
        var subject = Subject((_, _, token) => UntilCut(token), runLimit: TimeSpan.FromSeconds(1),
            memberLimit: TimeSpan.FromSeconds(10), preparing: () => prepared++);

        subject.Generate(LibraryModels.BuiltIn, [_candidates[0]]);
        subject.EndScope();

        Assert.Equal(1, prepared);
        Assert.Equal(GenerationOutcomeKind.Budget, subject.AnswerOf(_candidates[0].Target)!.Outcome.Kind);
        Assert.Equal(GenerationOutcome.BUDGET, GenerationCauses.Of(ModelGeneration.Generate,
            new TargetStanding(Lookup(subject.Layer(LibraryModels.BuiltIn, _candidates), _candidates[0]), false,
                               subject.AnswerOf(_candidates[0].Target))));
        Assert.Empty(Stored(_candidates[0]).Entries);
    }

    [Fact]
    public void Failure_after_preparation_keeps_its_coverage_reason_and_prevents_a_readers_finality()
    {
        var subject = Subject((request, answers, _) =>
        {
            if (request.MemberId == _candidates[0].Target.MemberId)
                throw new InvalidOperationException("generation failed after preparation");
            // As before, failed outcomes have no file entry; a view of no answer still cannot become final.
            Assert.Null(answers(_candidates[0].Target.MemberId, _candidates[0].Target.Key));
            return Model(request) with { DependencyViews = [new DependencyView(_candidates[0].Target, DependencyViewKind.NoAnswer, null)] };
        });

        subject.Generate(LibraryModels.BuiltIn, _candidates[..2]);
        subject.EndScope();

        Assert.Equal(GenerationOutcomeKind.Failed, subject.AnswerOf(_candidates[0].Target)!.Outcome.Kind);
        Assert.Equal(GenerationOutcomeKind.Model, subject.AnswerOf(_candidates[1].Target)!.Outcome.Kind);
        Assert.Equal(GenerationOutcome.FAILED, GenerationCauses.Of(ModelGeneration.Generate,
            new TargetStanding(Lookup(subject.Layer(LibraryModels.BuiltIn, _candidates), _candidates[0]), false,
                               subject.AnswerOf(_candidates[0].Target))));
        Assert.Empty(Stored(_candidates[0]).Entries);
    }

    [Fact]
    public void The_same_member_is_generated_again_and_stored_when_the_next_run_prepares_best_code()
    {
        var first = Subject((request, _, _) => Model(request));
        first.Generate(LibraryModels.BuiltIn, [_candidates[0]]);
        first.EndScope();
        Assert.False(File.Exists(_candidates[0].Target.Key.PathUnder(Repository)));

        var calls = 0;
        var second = Subject((request, _, _) =>
        {
            calls++;
            return Model(request);
        }, runOnly: _ => false);
        second.Generate(LibraryModels.BuiltIn, [_candidates[0]]);
        second.EndScope();

        Assert.Equal(1, calls);
        Assert.NotNull(Stored(_candidates[0]).Entries[_candidates[0].Target.MemberId].Model);
        Assert.Equal(GenerationOutcomeKind.Model, first.AnswerOf(_candidates[0].Target)!.Outcome.Kind);
    }

    [Fact]
    public void Budget_exhausted_before_preparation_does_not_obtain_a_code_kind()
    {
        var subject = Subject((_, _, _) => throw new InvalidOperationException("must not generate"), runLimit: TimeSpan.Zero,
            preparing: () => throw new InvalidOperationException("must not prepare"));

        subject.Generate(LibraryModels.BuiltIn, [_candidates[0]]);
        subject.EndScope();

        Assert.Equal(GenerationOutcomeKind.Budget, subject.AnswerOf(_candidates[0].Target)!.Outcome.Kind);
        Assert.False(File.Exists(_candidates[0].Target.Key.PathUnder(Repository)));
    }

    [Fact]
    public async Task Source_preparation_controls_storage_even_when_another_run_reuses_the_compilation()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address, documentCount: 1);
        var directory = _install.SharedFramework("8.0.5", runtime: true,
            images: new Dictionary<string, byte[]> { ["library"] = File.ReadAllBytes(library.Dll) });
        var compilation = CSharpCompilation.Create("App", references:
            [.. EmittedAssemblies.RuntimeReferences, MetadataReference.CreateFromFile(Path.Combine(directory, "library.dll"))]);
        var member = compilation.GetTypeByMetadataName("C0")!.GetMembers("M").OfType<IMethodSymbol>().Single();
        var target = Assert.IsType<GenerationTarget>(GenerationTargets.Of(member, compilation, _install.Resolver(), CancellationToken.None, generationFrameworkMajor: 8));
        var candidate = new GenerationCandidate(compilation, member, target, false);
        using var offline = new LibrarySources(true, library.Cache, new Uri(server.Address, "symbols/"), new Uri(server.Address, "nuget/"), httpsOnly: false);
        using var first = GenerationPass.Create(new GenerationSetup(ModelGeneration.Generate, _install.Resolver(), Sources: offline), Repository, CancellationToken.None)!;

        var layer = first.Prepare(LibraryModels.BuiltIn, [candidate]);

        Assert.Empty(server.Requests);
        Assert.Empty(layer.Diagnostics);
        Assert.Equal(1, first.GeneratedInRun([candidate]));
        Assert.Equal(StoredGeneratedKind.Model, Lookup(layer, candidate).Kind);
        Assert.Empty(GenerationCauses.Items(ModelGeneration.Generate, first.Reached(LibraryModels.BuiltIn, layer, [candidate])));
        Assert.False(File.Exists(target.Key.PathUnder(Repository)));

        // Both symbol servers now answer 404: best decompiled code, using the compilation still owned by the first run.
        using var online = new LibrarySources(false, library.Cache, new Uri(server.Address, "symbols/"), new Uri(server.Address, "nuget/"), httpsOnly: false);
        using var second = GenerationPass.Create(new GenerationSetup(ModelGeneration.Generate, _install.Resolver(), Sources: online), Repository, CancellationToken.None)!;
        var secondLayer = second.Prepare(LibraryModels.BuiltIn, [candidate]);

        Assert.NotEmpty(server.Requests);
        Assert.Equal(1, second.GeneratedInRun([candidate]));
        Assert.Equal(StoredGeneratedKind.Model, Lookup(secondLayer, candidate).Kind);
        Assert.NotNull(GeneratedModelFiles.Read(Repository, target.Key).Entries[target.MemberId].Model);
        Assert.False(Directory.Exists(library.Cache));
    }

    [Fact]
    public async Task Transient_methodless_absence_makes_the_fallback_and_its_reader_run_only()
    {
        await MethodlessAbsence(SourceOutcome.Transient, refused: true, timeout: false);
    }

    [Fact]
    public async Task Persistent_methodless_absence_keeps_the_fallback_and_its_reader_final()
    {
        await MethodlessAbsence(SourceOutcome.Persistent, refused: true, timeout: false);
    }

    [Fact]
    public async Task Synthetic_timeout_after_a_transient_methodless_fallback_is_not_stored()
    {
        await MethodlessAbsence(SourceOutcome.Transient, refused: true, timeout: true);
    }

    [Fact]
    public async Task Synthetic_timeout_after_sources_compile_with_transient_methodless_absence_is_stored_including_a_cache_hit()
    {
        await MethodlessAbsence(SourceOutcome.Transient, refused: false, timeout: true);
    }

    [Fact]
    public async Task Synthetic_timeout_after_sources_compile_with_offline_methodless_absence_is_stored_including_a_cache_hit()
    {
        await MethodlessAbsence(SourceOutcome.UnavailableOffline, refused: false, timeout: true);
    }

    private async Task MethodlessAbsence(SourceOutcome absence, bool refused, bool timeout)
    {
        await using var server = new LoopbackSymbolServer();
        var source = "public " + (refused ? "class Api : IMarker" : "static class Api") +
            " { public static void Run(System.Action action) => action(); }";
        using var fixture = new LibrarySourceCompilationTests.Fixture(source, "public interface IMarker { }",
            sourceServer: server.Address, extraDocument: "public interface IExtra { }");
        server.Replies["/sources/AssemblyInfo.cs"] = new(absence == SourceOutcome.Transient ? 503 : 404);
        server.Replies["/sources/Extra.cs"] = new(404);
        using var sources = new LibrarySources(absence == SourceOutcome.UnavailableOffline,
            Path.Combine(fixture.Install.Root, "sources"), httpsOnly: false);
        var compilation = CSharpCompilation.Create("App", references:
            [.. EmittedAssemblies.RuntimeReferences, MetadataReference.CreateFromFile(fixture.Assembly.Path)]);
        var member = compilation.GetTypeByMetadataName("Api")!.GetMembers("Run").OfType<IMethodSymbol>().Single();
        var target = Assert.IsType<GenerationTarget>(GenerationTargets.Of(member, compilation, fixture.Install.Resolver(),
            CancellationToken.None, generationFrameworkMajor: 8));
        var candidate = new GenerationCandidate(compilation, member, target, false);
        using var owned = fixture.Ownership.Open();
        // Keep the first completed compilation alive: the second caller must learn its own outcome from the cache hit.
        for (var attempt = 0; attempt < (timeout && !refused ? 2 : 1); attempt++)
        {
            var repository = Path.Combine(fixture.Install.Root, "repository" + attempt);
            GenerationPreparation? prepared = null;
            GeneratedAnswer? generated = null;
            var subject = new RunGeneration(repository, CancellationToken.None, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1), _clock,
                (_, _, _) => throw new InvalidOperationException("must use preparation"), (_, _) => 0,
                prepare: (request, token) =>
                {
                    if (request.MemberId != target.MemberId)
                        return (() => false, (answers, _) => Model(request) with
                        {
                            DependencyViews = [Read(answers, candidate, refusal: timeout || generated?.Model is null)]
                        });
                    prepared = ModelGenerator.Prepare(request, fixture.Install.Resolver(), sources, token);
                    Assert.Equal(LibraryCodeKind.OriginalSources, prepared!.Sources!.Kind);
                    Assert.Contains(absence, prepared.Sources.MissingMethodlessDocuments);
                    Assert.Equal(2, prepared.Sources.MissingMethodlessDocuments.Count);
                    return (() => prepared.RunOnly, (answers, memberToken) =>
                    {
                        var probe = new LibraryCompilationProbe
                        {
                            Started = () =>
                            {
                                if (timeout && refused && prepared.Code?.Kind == LibraryCodeKind.DecompiledForRun)
                                {
                                    _clock.Advance(TimeSpan.FromSeconds(2));
                                    memberToken.ThrowIfCancellationRequested();
                                }
                            }
                        };
                        generated = ModelGenerator.TraceWithin(request, fixture.Install.Resolver(), TimeSpan.FromMinutes(1),
                            cancellationToken: memberToken, compilationProbe: probe, prepared: prepared).Answer;
                        sources.RecordCode(fixture.Assembly.Path, generated.Generation.Code!);
                        if (timeout)
                            return UntilCut(memberToken);
                        return generated;
                    });
                });

            subject.Generate(LibraryModels.BuiltIn, [candidate]);
            subject.Generate(LibraryModels.BuiltIn, [_candidates[3]]);
            subject.EndScope();

            var expectedRunOnly = refused && absence != SourceOutcome.Persistent;
            Assert.Equal(expectedRunOnly, prepared!.RunOnly);
            Assert.Equal(refused ? expectedRunOnly ? LibraryCodeKind.DecompiledForRun : LibraryCodeKind.DecompiledBest
                : LibraryCodeKind.OriginalSources, prepared.Code!.Kind);
            Assert.Equal(timeout ? GenerationOutcomeKind.Timeout : generated!.Model is null ? GenerationOutcomeKind.Refusal : GenerationOutcomeKind.Model,
                subject.AnswerOf(target)!.Outcome.Kind);
            var layer = subject.Layer(LibraryModels.BuiltIn, [candidate, _candidates[3]]);
            Assert.NotEqual(StoredGeneratedKind.Absent, Lookup(layer, candidate).Kind);
            Assert.Equal(StoredGeneratedKind.Model, Lookup(layer, _candidates[3]).Kind);
            Assert.Equal(!expectedRunOnly, GeneratedModelFiles.Read(repository, target.Key).Entries.ContainsKey(target.MemberId));
            Assert.Equal(!expectedRunOnly, GeneratedModelFiles.Read(repository, _candidates[3].Target.Key).Entries.ContainsKey(_candidates[3].Target.MemberId));
            if (!timeout)
                Assert.Equal(refused && !expectedRunOnly ? 1 : 0, sources.Statistics.Libraries.DecompiledBest);
        }
    }

    private RunGeneration Subject(Func<GenerationRequest, DependencyAnswers, CancellationToken, GeneratedAnswer> generate,
                                  Func<GenerationRequest, bool>? runOnly = null, TimeSpan? runLimit = null,
                                  TimeSpan? memberLimit = null, Action? preparing = null) =>
        new(Repository, CancellationToken.None, runLimit ?? TimeSpan.FromSeconds(100), memberLimit ?? TimeSpan.FromSeconds(1), _clock,
            (_, _, _) => throw new InvalidOperationException("must use the prepared generation"), (_, _) => 0,
            prepare: (request, _) =>
            {
                preparing?.Invoke();
                return (() => runOnly?.Invoke(request) ?? request.MemberId == _candidates[0].Target.MemberId,
                        (answers, token) => generate(request, answers, token));
            });

    private GeneratedAnswer UntilCut(CancellationToken token)
    {
        _clock.Advance(TimeSpan.FromSeconds(2));
        token.ThrowIfCancellationRequested();
        throw new InvalidOperationException("the fake clock must cut the generation");
    }

    private static DependencyView Read(DependencyAnswers answers, GenerationCandidate candidate, bool refusal = false)
    {
        var entry = Assert.IsType<GeneratedModelEntry>(answers(candidate.Target.MemberId, candidate.Target.Key));
        if (refusal) Assert.NotNull(entry.Refusal);
        else Assert.NotNull(entry.Model);
        return new DependencyView(candidate.Target, refusal ? DependencyViewKind.Refusal : DependencyViewKind.Model, entry);
    }

    private static StoredGeneratedAnswer Lookup(ScopeLayer layer, GenerationCandidate candidate) =>
        Assert.IsType<StoredGeneratedAnswer>(layer.Table.Lookup(candidate.Compilation, candidate.Member, candidate.Target.LookupId));

    private GeneratedModelFile Stored(GenerationCandidate candidate) => GeneratedModelFiles.Read(Repository, candidate.Target.Key);

    private static GeneratedAnswer Model(GenerationRequest request) =>
        new(5, request.MemberId, new GenerationAssembly(request.AssemblyName, request.Version, request.PackageId), null, null,
            new LibraryModel(request.MemberId, [new SupportedAssemblyVersion(PACKAGE, new Version(1, 0, 0, 0), new Version(1, 0, 0, 1))],
                [LibraryEffect.DeepReadOf("value")]), null,
            new GenerationRecord(null, null, [], 0, [], new Dictionary<string, string>(), [], 0, [], new Dictionary<string, IReadOnlyList<string>>(), 0, 0));

    private static GeneratedAnswer Refusal(GenerationRequest request) =>
        Model(request) with { Model = null, Reason = GenerationReasons.DRIVER_NOT_SYNTHESIZED };

    public void Dispose() => _install.Dispose();
}
