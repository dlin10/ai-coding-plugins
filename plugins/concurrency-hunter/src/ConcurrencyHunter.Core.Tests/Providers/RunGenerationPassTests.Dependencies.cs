using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed partial class RunGenerationPassTests
{
    [Fact]
    public async Task Refused_member_finishes_its_dependencies_without_reanalysing_the_scope()
    {
        var y = Dependency("Y");
        var image = Library("main");
        var result = await Run(Solution("Acme.Api.Touch(_state);", image), ModelGeneration.Generate, (request, source, _) =>
            request.MemberId == y.MemberId ? Refusal(request) : WithViews(Refusal(request), View(y, source)));

        Assert.Equal(1, _analyses);
        Assert.Equal(2, _requested.Count);
        Assert.NotNull(Stored(y)?.Refusal);
        Assert.NotNull(Assert.Single(GeneratedModelFiles.Read(_repository, KeyOf(image)).Entries).Value.Refusal);
        Assert.Single(Assert.Single(result.Coverage).GenerationRefusals);
        Assert.Equal(0, Counter(result, CoverageCounters.GENERATED_IN_RUN));
    }

    [Fact]
    public async Task Regeneration_keeps_scope_delegate_priority_before_dependency_targets()
    {
        var y = Dependency("Y");
        var w = Dependency("W");
        var earlier = "M:A.First.Run(System.Object)";
        w = w with { MemberId = earlier, LookupId = earlier, Request = w.Request with { MemberId = earlier } };
        _ = await Run(Solution("Acme.Api.Run(() => { });"), ModelGeneration.Generate, (request, source, _) =>
        {
            if (request.MemberId == w.MemberId)
                return DependencyModel(request);
            if (request.MemberId == y.MemberId)
                return WithViews(DependencyModel(request), View(w, source));
            return WithViews(Model(request), View(y, source));
        });

        Assert.Equal(["M:Acme.Api.Run(System.Action)", y.MemberId, "M:Acme.Api.Run(System.Action)", w.MemberId, y.MemberId], _requested);
    }

    [Fact]
    public async Task Regenerated_model_with_other_effects_is_analysed_again()
    {
        var y = Dependency("Y");
        var solution = Solution("Acme.Api.Touch(_state);");
        var result = await Run(solution, ModelGeneration.Generate, (request, source, _) =>
        {
            if (request.MemberId == y.MemberId)
                return DependencyModel(request);
            var view = View(y, source);
            return WithViews(view.ModelText is null ? Model(request) : Writing(request), view);
        });

        Assert.Equal(3, _analyses);
        Assert.Equal(2, _requested.Count(member => member.Contains("Touch", StringComparison.Ordinal)));
        Assert.Equal((1, 1), (Counter(result, CoverageCounters.KNOWN_CALL_GENERATED), Counter(result, CoverageCounters.GENERATED_IN_RUN)));
        Assert.Contains(result.Accesses, access => access.Operation == AccessOperation.Write);
        Assert.DoesNotContain(result.Accesses, access => access.Operation == AccessOperation.UnknownEffect);
        Assert.NotEmpty(CountFindings(result));
        var finalOnly = await Run(solution, ModelGeneration.ReadOnly);
        Assert.Equal(Fingerprints(finalOnly), Fingerprints(result));
        Assert.Equal(finalOnly.Accesses.Select(access => (access.Operation, access.Source)), result.Accesses.Select(access => (access.Operation, access.Source)));
        Assert.Equal(Counter(finalOnly, CoverageCounters.PAIRS_REMOVED_GENERATED), Counter(result, CoverageCounters.PAIRS_REMOVED_GENERATED));
        Assert.Empty(Assert.Single(result.Coverage).GenerationRefusals);
        Assert.NotNull(Stored(y));
    }

    [Fact]
    public async Task Model_replaced_by_a_refusal_is_analysed_again()
    {
        var y = Dependency("Y");
        var solution = Solution("Acme.Api.Touch(_state);");
        var off = await Run(solution, ModelGeneration.None);
        _analyses = 0;
        var result = await Run(solution, ModelGeneration.Generate, (request, source, _) =>
        {
            if (request.MemberId == y.MemberId)
                return DependencyModel(request);
            var view = View(y, source);
            return WithViews(view.ModelText is null ? Model(request) : Refusal(request), view);
        });

        Assert.Equal(3, _analyses);
        Assert.Equal(Fingerprints(off), Fingerprints(result));
        Assert.Equal((0, 0, 0), (Counter(result, CoverageCounters.KNOWN_CALL_GENERATED), Counter(result, CoverageCounters.GENERATED_IN_RUN),
                                 Counter(result, CoverageCounters.PAIRS_REMOVED_GENERATED)));
        Assert.Contains(result.Accesses, access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, Assert.Single(Assert.Single(result.Coverage).GenerationRefusals).Cause);
    }

    [Fact]
    public async Task Changed_model_of_a_seen_dependency_regenerates_the_member()
    {
        var (y, z) = (Dependency("Y"), Dependency("Z"));
        var result = await Run(Solution("Acme.Api.Touch(_state);"), ModelGeneration.Generate, (request, source, _) =>
        {
            if (request.MemberId == z.MemberId)
                return DependencyModel(request);
            if (request.MemberId == y.MemberId)
            {
                var seen = View(z, source);
                return WithViews(DependencyModel(request, writes: seen.ModelText is not null), seen);
            }
            var view = View(y, source);
            var writes = view.Entry?.Model?.Effects.Any(effect => effect.Kind == LibraryEffectKind.WriteArgument) == true;
            return WithViews(writes ? Writing(request) : Model(request), view);
        });

        Assert.Equal(2, _requested.Count(member => member == y.MemberId));
        Assert.Equal(3, _requested.Count(member => member.Contains("Touch", StringComparison.Ordinal)));
        Assert.Equal(3, _analyses); // The first repeat of X has the same bytes; only Y's changed bytes change X's effects.
        Assert.NotEmpty(CountFindings(result));
        Assert.DoesNotContain(result.Accesses, access => access.Operation == AccessOperation.UnknownEffect);
        Assert.NotNull(Stored(y));
        Assert.NotNull(Stored(z));
    }

    [Fact]
    public async Task Old_answer_waiting_for_its_regeneration_is_not_written()
    {
        var y = Dependency("Y");
        var image = Library("main");
        var calls = 0;
        var alreadyStored = new List<bool>();
        var result = await Run(Solution("Acme.Api.Touch(_state);", image), ModelGeneration.Generate, (request, source, _) =>
        {
            if (request.MemberId == y.MemberId)
                return DependencyModel(request);
            calls++;
            alreadyStored.Add(GeneratedModelFiles.Read(_repository, KeyOf(image)).Entries.ContainsKey(request.MemberId));
            var view = View(y, source);
            return WithViews(Model(request), view);
        });

        Assert.Equal(2, calls);
        Assert.All(alreadyStored, kept => Assert.False(kept));
        Assert.Equal(2, _analyses); // Regeneration with the same bytes needs no extra scope analysis.
        Assert.Equal(1, Counter(result, CoverageCounters.GENERATED_IN_RUN));
        Assert.NotNull(GeneratedModelFiles.Read(_repository, KeyOf(image)).Entries["M:Acme.Api.Touch(System.Object)"].Model);
    }

    [Fact]
    public async Task Budget_spent_before_a_regeneration_leaves_the_old_answer_unkept()
    {
        var y = Dependency("Y");
        var clock = new FakeClock();
        var image = Library("main");
        var result = await Run(Solution("Acme.Api.Touch(_state);", image), ModelGeneration.Generate, (request, source, _) =>
        {
            if (request.MemberId == y.MemberId)
            {
                clock.Advance(RunGeneration.RUN_LIMIT);
                return DependencyModel(request);
            }
            return WithViews(Model(request), View(y, source));
        }, time: clock);

        Assert.Equal(1, _requested.Count(member => member.Contains("Touch", StringComparison.Ordinal)));
        Assert.Equal((1, 1), (Counter(result, CoverageCounters.KNOWN_CALL_GENERATED), Counter(result, CoverageCounters.GENERATED_IN_RUN)));
        Assert.Empty(GeneratedModelFiles.Read(_repository, KeyOf(image)).Entries);
        Assert.NotNull(Stored(y));
        Assert.Equal(2, _analyses);
    }

    [Fact]
    public async Task Member_cut_by_its_limit_after_meeting_a_dependency_is_kept_as_timeout()
    {
        var y = Dependency("Y");
        var clock = new FakeClock();
        var image = Library("main");
        var result = await Run(Solution("Acme.Api.Touch(_state);", image), ModelGeneration.Generate, (request, source, token) =>
        {
            Assert.Null(View(y, source).Entry);
            return RunUntilCut(clock, token);
        }, time: clock);

        Assert.Single(_requested);
        Assert.Equal(1, _analyses);
        Assert.Null(Stored(y));
        var entry = Assert.Single(GeneratedModelFiles.Read(_repository, KeyOf(image)).Entries).Value;
        Assert.Equal(GenerationOutcome.TIMEOUT, entry.Refusal!.Reason);
        Assert.Equal(GenerationOutcome.TIMEOUT, Assert.Single(Assert.Single(result.Coverage).GenerationRefusals).Cause);
    }

    [Fact]
    public async Task Cancellation_while_a_dependency_is_generated_writes_no_answer_that_rests_on_it()
    {
        var y = Dependency("Y");
        var image = Library("main");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Solution("Acme.Api.Touch(_state);", image), ModelGeneration.Generate,
            (request, source, token) =>
            {
                if (request.MemberId == y.MemberId)
                {
                    cancel.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return WithViews(Model(request), View(y, source));
            }, token: cancel.Token));

        Assert.Equal(2, _requested.Count);
        Assert.Empty(GeneratedModelFiles.Read(_repository, KeyOf(image)).Entries);
        Assert.Null(Stored(y));
    }

    [Fact]
    public async Task Dependency_that_becomes_a_scope_target_counts_once_and_is_compared()
    {
        var image = Library("main");
        var key = KeyOf(image);
        var poke = new GenerationTarget("M:Acme.Api.Poke(System.Object)", "M:Acme.Api.Poke(System.Object)", key,
                                       new GenerationRequest(PACKAGE, "1.0.0", "M:Acme.Api.Poke(System.Object)", PACKAGE, "net8.0"));
        var z = Dependency("Z");
        var pokeAnalyses = new List<int>();
        var result = await Run(Chain(image), ModelGeneration.Generate, (request, source, _) =>
        {
            if (request.MemberId == z.MemberId)
                return DependencyModel(request);
            if (request.MemberId == poke.MemberId)
            {
                pokeAnalyses.Add(_analyses);
                var seen = View(z, source);
                return WithViews(seen.ModelText is null ? Model(request) : Writing(request), seen);
            }
            var view = View(poke, source);
            return WithViews(view.ModelText is null ? Model(request) : ModelWithEquals(request), view);
        });

        Assert.Equal(2, _requested.Count(member => member == poke.MemberId));
        Assert.Equal([2, 3], pokeAnalyses); // Poke is generated before the third analysis first reaches its call.
        Assert.Equal(2, Counter(result, CoverageCounters.GENERATED_IN_RUN));
        Assert.Equal(2, Counter(result, CoverageCounters.KNOWN_CALL_GENERATED));
        Assert.Empty(Assert.Single(result.Coverage).GenerationRefusals);
        Assert.Equal(4, _analyses);
        Assert.NotEmpty(CountFindings(result));
        Assert.Equal(2, GeneratedModelFiles.Read(_repository, key).Entries.Count);
    }

    [Fact]
    public async Task Generation_of_a_real_library_takes_the_generated_model_of_its_dependency()
    {
        using var install = new GenerationInstall();
        install.SharedFramework("8.0.5", runtime: true);
        var b = Emit("B", "namespace B { public static class Api { public static void Read(object item) { _ = item.GetHashCode(); } } }");
        var dependencyPath = ReferenceScopes.AddAsset(install, "B", "1.0.0", "net8.0", "B", b);
        var a = Emit(PACKAGE, "namespace Acme { public static class Api { public static void Touch(object value) { B.Api.Read(value); } } }",
                     MetadataReference.CreateFromFile(dependencyPath));
        ReferenceScopes.AddAsset(install, PACKAGE, "1.0.0", "net8.0", PACKAGE, a,
            "<dependencies><group targetFramework='net8.0'><dependency id='B' version='[1.0.0]' /></group></dependencies>");
        var solution = FixtureSolution.Create(new FixtureOptions
        {
            MetadataReferences = [ReferenceScopes.Reference(a, ReferenceScopes.PackagePath(install, PACKAGE, "1.0.0", "lib", "net8.0", PACKAGE)),
                ReferenceScopes.Reference(EmittedAssemblies.Image("Fixture.Pack"), ReferenceScopes.PackFile(install, 8, "Microsoft.NETCore.App.Ref", "Fixture.Pack.dll"))]
        }, ("Case.cs", Source("Acme.Api.Touch(_state);")));
        var result = await Run(solution, ModelGeneration.Generate,
            (request, source, token) => ModelGenerator.Generate(request, install.Resolver(), token, source), install: install.Resolver());

        Assert.Contains("M:B.Api.Read(System.Object)", _requested);
        Assert.Equal(2, _requested.Count(member => member == "M:Acme.Api.Touch(System.Object)"));
        var model = GeneratedModelFiles.Read(_repository, KeyOf(a)).Entries["M:Acme.Api.Touch(System.Object)"].Model;
        Assert.NotNull(model);
        Assert.Contains(LibraryEffect.DeepReadOf("value"), model.Effects);
        Assert.Equal((1, 1), (Counter(result, CoverageCounters.GENERATED_IN_RUN), Counter(result, CoverageCounters.KNOWN_CALL_GENERATED)));
        Assert.Empty(Assert.Single(result.Coverage).GenerationRefusals);
    }

    private static GenerationTarget Dependency(string name)
    {
        var id = $"M:Dependency.{name}.Run(System.Object)";
        var key = GeneratedFileKey.ForFramework("Dependency." + name, new Version(1, 0, 0, 0), "Microsoft.NETCore.App", 8);
        return new GenerationTarget(id, id, key, new GenerationRequest(key.AssemblyName, "1.0.0", id, null, null));
    }

    private static DependencyView View(IdentifiedGenerationTarget target, DependencyAnswers source)
    {
        var entry = source(target.MemberId, target.Key);
        return new DependencyView(target, entry?.Model is not null ? DependencyViewKind.Model : entry?.Refusal is not null ? DependencyViewKind.Refusal :
            target is UnavailableGenerationTarget ? DependencyViewKind.NoImplementation : DependencyViewKind.NoAnswer, entry);
    }

    private static GeneratedAnswer WithViews(GeneratedAnswer answer, params DependencyView[] views) => answer with { DependencyViews = views };

    private static GeneratedAnswer Writing(GenerationRequest request) => Answer(request, new LibraryModel(request.MemberId, [Range()], [LibraryEffect.WriteOf("value")]));

    private static GeneratedAnswer DependencyModel(GenerationRequest request, bool writes = false) => Answer(request, new LibraryModel(request.MemberId,
        [new SupportedAssemblyVersion(request.AssemblyName, new Version(1, 0, 0, 0), new Version(1, 0, 0, 1))],
        [writes ? LibraryEffect.WriteOf("item") : LibraryEffect.DeepReadOf("item")]));

    private GeneratedModelEntry? Stored(IdentifiedGenerationTarget target) => GeneratedModelFiles.Read(_repository, target.Key).Entries.GetValueOrDefault(target.MemberId);

    private static GeneratedAnswer RunUntilCut(FakeClock clock, CancellationToken token)
    {
        for (var seconds = 0; seconds < 1000; seconds++)
        {
            token.ThrowIfCancellationRequested();
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        throw new InvalidOperationException("The member was never cut.");
    }
}
