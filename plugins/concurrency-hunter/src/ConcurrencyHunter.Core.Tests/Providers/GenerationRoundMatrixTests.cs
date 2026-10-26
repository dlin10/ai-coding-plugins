using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class GenerationRoundMatrixTests
{
    private enum Outcome { Model, Refusal, Timeout }
    private enum PathOfY
    {
        StoredModel, StoredRefusal, RunFinalModel, RunNonfinalModel, NoThenModel, NoThenRefusal, NoThenBudget, NoThenFailed,
        Unavailable, UnavailableStoredModel, ModelChanges, ModelRefused, RefusalBecomesModel, SameX, FailedX, TimeoutX
    }
    private enum Timing { Before, After }
    private enum Chain { Direct, ZRefusal, ZModel, ZBudget, Cycle }
    private enum Second { None, Refusal, Budget, Unavailable }
    private enum Budget { Enough, BeforeRetry, DuringRetry }
    private sealed record Cell(Outcome Outcome, PathOfY Path, Timing Timing, Chain Chain, Second Second, Budget Budget);

    private static readonly Cell[] Cells = (from outcome in Enum.GetValues<Outcome>()
                                            from path in Enum.GetValues<PathOfY>()
                                            from timing in Enum.GetValues<Timing>()
                                            from chain in Enum.GetValues<Chain>()
                                            from second in Enum.GetValues<Second>()
                                            from budget in Enum.GetValues<Budget>()
                                            select new Cell(outcome, path, timing, chain, second, budget)).Where(Allowed).ToArray();

    private static bool Stored(PathOfY path) => path is PathOfY.StoredModel or PathOfY.StoredRefusal or PathOfY.UnavailableStoredModel;
    private static bool Missing(PathOfY path) => path is PathOfY.Unavailable or PathOfY.UnavailableStoredModel;
    private static bool ChangesY(Cell cell) => cell.Chain == Chain.ZModel;
    private static bool FirstYModel(Cell cell) => cell.Path is not (PathOfY.StoredRefusal or PathOfY.NoThenRefusal or PathOfY.NoThenBudget or
        PathOfY.NoThenFailed or PathOfY.Unavailable or PathOfY.RefusalBecomesModel);

    private static bool Allowed(Cell cell)
    {
        // Cancellation is covered by RunGenerationTests and the dependency cancellation test in RunGenerationPassTests.
        // A timeout is thrown after reading Y: it has no returned views, so no after-target or second dependency can be built.
        if (cell.Outcome == Outcome.Timeout)
            return (cell.Path is PathOfY.StoredModel or PathOfY.StoredRefusal) && cell.Timing == Timing.Before &&
                   cell.Chain == Chain.Direct && cell.Second == Second.None && cell.Budget == Budget.Enough;
        // Budget variants isolate one repeat, after X first sees no answer and Y gives a model.
        if (cell.Budget != Budget.Enough)
            return cell.Path == PathOfY.NoThenModel && cell.Timing == Timing.After && cell.Chain == Chain.Direct && cell.Second == Second.None;
        // A cycle needs X's first answer in the run, so Y cannot already have an answer before X.
        if (cell.Chain == Chain.Cycle)
            return cell.Path == PathOfY.NoThenModel && cell.Timing == Timing.After;
        // Stored and unavailable answers have no in-run history or dependency views.
        if (Stored(cell.Path) || Missing(cell.Path))
            return cell.Timing == Timing.Before && cell.Chain == Chain.Direct;
        // These paths construct Y before X, with either no views or a non-final model made final by the same-looking Z refusal.
        if (cell.Path == PathOfY.RunFinalModel)
            return cell.Timing == Timing.Before && cell.Chain == Chain.Direct;
        if (cell.Path == PathOfY.RunNonfinalModel)
            return cell.Timing == Timing.Before && cell.Chain == Chain.ZRefusal && cell.Second != Second.Budget;
        // Y's model/refusal transitions require Z's model. W cannot spend the budget before that transition happens.
        if (cell.Path is PathOfY.ModelChanges or PathOfY.ModelRefused or PathOfY.RefusalBecomesModel)
            return cell.Chain == Chain.ZModel && cell.Second != Second.Budget;
        // X's special repeat outcomes need Y's model and remaining budget; a W budget cut would prevent that repeat.
        if (cell.Path is PathOfY.SameX or PathOfY.FailedX or PathOfY.TimeoutX)
            return cell.Timing == Timing.After && cell.Chain == Chain.Direct && cell.Second != Second.Budget;
        // A cut Y cannot have views and spends the whole remainder before W; a W answer cannot be constructed after it.
        if (cell.Path == PathOfY.NoThenBudget)
            return cell.Timing == Timing.After && cell.Chain == Chain.Direct && cell.Second is Second.None or Second.Unavailable;
        // The failed/refused paths have no dependency views, while W remains an independent branch.
        if (cell.Path is PathOfY.NoThenFailed or PathOfY.NoThenRefusal)
            return cell.Timing == Timing.After && cell.Chain == Chain.Direct;
        // W's cut would prevent the Z answer specified by a chain; no such chain is built with a W budget cut.
        return cell.Path == PathOfY.NoThenModel && cell.Timing == Timing.After && (cell.Chain == Chain.Direct || cell.Second != Second.Budget);
    }

    [Fact]
    public void Every_cell_gets_its_expected_answer()
    {
        using var fixture = new Fixture();
        foreach (var cell in Cells)
        {
            using var run = fixture.Run(cell);
            var expected = Expected(cell);
            Assert.True(expected == run.Observed, $"{cell}: expected {expected}; got {run.Observed}");
            var answer = Assert.Single(run.ScopeTargets).Standing.Generated!;
            if (run.Observed.KeptX)
            {
                var stored = run.Stored(fixture.X.Target)!;
                Assert.Equal(GeneratedModelFiles.ModelText(GeneratedModelFiles.EntryOf(answer, "run")!), GeneratedModelFiles.ModelText(stored));
                Assert.Equal(answer.Refusal?.Reason, stored.Refusal?.Reason);
            }
            if (cell.Path == PathOfY.TimeoutX || cell.Outcome == Outcome.Timeout)
                Assert.Equal(GenerationOutcome.TIMEOUT, answer.Refusal!.Reason);
            if (cell.Path == PathOfY.FailedX || cell.Budget != Budget.Enough)
                Assert.Equal(cell.Outcome == Outcome.Model, answer.Refusal is null); // The previous model survives a stopped repeat.
            Assert.Empty(run.Pass.Wanted(LibraryModels.BuiltIn, [fixture.X]));
            var wanted = run.ReadOnlyWanted(fixture.X);
            Assert.Equal(run.Observed.KeptX ? 0 : 1, wanted.Count);
            Assert.All(wanted, target => Assert.Equal(fixture.X.Target.MemberId, target.MemberId));
            Assert.Equal(run.Observed.KnownX ? 1 : 0, run.ScopeTargets.Count(target => target.Standing.GeneratedModelApplies));
            Assert.All(run.ScopeTargets, target => Assert.Equal(fixture.X.Target.MemberId, target.Target.MemberId));
        }
    }

    [Fact]
    public void No_cell_keeps_an_answer_that_rests_on_a_reason_of_the_run()
    {
        using var fixture = new Fixture();
        foreach (var cell in Cells)
        {
            using var run = fixture.Run(cell);
            if (!Expected(cell).FinalX)
            {
                Assert.False(run.Observed.FinalX, cell.ToString());
                Assert.Null(run.Stored(fixture.X.Target));
            }
            Assert.False(run.Pass.HasWork, cell.ToString());
            Assert.True(run.Rounds < 12, cell.ToString());
        }
    }

    [Fact]
    public void Every_axis_value_occurs()
    {
        Assert.Equal(Cells.Length, Cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<Outcome>(), Cells.Select(cell => cell.Outcome).Distinct().Order());
        Assert.Equal(Enum.GetValues<PathOfY>(), Cells.Select(cell => cell.Path).Distinct().Order());
        Assert.Equal(Enum.GetValues<Timing>(), Cells.Select(cell => cell.Timing).Distinct().Order());
        Assert.Equal(Enum.GetValues<Chain>(), Cells.Select(cell => cell.Chain).Distinct().Order());
        Assert.Equal(Enum.GetValues<Second>(), Cells.Select(cell => cell.Second).Distinct().Order());
        Assert.Equal(Enum.GetValues<Budget>(), Cells.Select(cell => cell.Budget).Distinct().Order());
        Assert.Contains(Cells, cell => cell.Path == PathOfY.RunNonfinalModel && cell.Chain == Chain.ZRefusal);
        Assert.Contains(Cells, cell => cell.Path == PathOfY.RefusalBecomesModel);
        Assert.Contains(Cells, cell => cell.Path == PathOfY.SameX);
        Assert.Contains(Cells, cell => cell.Path == PathOfY.FailedX);
        Assert.Contains(Cells, cell => cell.Path == PathOfY.TimeoutX);
    }

    private sealed record Observation(int CallsX, int CallsY, bool FinalX, bool KeptX, bool KnownX, bool KeptY, bool KeptZ, bool KeptW, int GeneratedInScope);

    private static Observation Expected(Cell cell)
    {
        if (cell.Outcome == Outcome.Timeout)
            return new(1, 0, true, true, false, true, false, false, 0);
        var cycle = cell.Chain == Chain.Cycle;
        var unavailableY = cell.Path == PathOfY.Unavailable;
        var stoppedY = cell.Path is PathOfY.NoThenBudget or PathOfY.NoThenFailed;
        var stoppedW = cell.Second is Second.Budget or Second.Unavailable;
        var stoppedZ = cell.Chain == Chain.ZBudget;
        var changes = ChangesY(cell);
        var callsY = Stored(cell.Path) || Missing(cell.Path) ? 0 : changes ? 2 : 1;
        var callsX = 1;
        if (!cycle && cell.Budget != Budget.BeforeRetry && cell.Second != Second.Budget)
        {
            if (cell.Timing == Timing.After && FirstYModel(cell))
                callsX++;
            if (changes)
                callsX++;
        }
        var timeoutRetry = cell.Path == PathOfY.TimeoutX;
        var final = timeoutRetry || !cycle && !unavailableY && !stoppedY && !stoppedW && !stoppedZ &&
                    cell.Budget == Budget.Enough && cell.Path != PathOfY.FailedX;
        var known = cell.Outcome == Outcome.Model && !timeoutRetry;
        return new(callsX, callsY, final, final, known, !cycle && !unavailableY && !stoppedY && !stoppedZ,
                   cell.Chain is Chain.ZModel or Chain.ZRefusal, cell.Second == Second.Refusal, known ? 1 : 0);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly GenerationInstall _install = new();
        internal GenerationCandidate X { get; }
        internal GenerationCandidate Y { get; }
        internal GenerationTarget Z { get; }
        internal GenerationTarget W { get; }

        internal Fixture()
        {
            _install.SharedFramework("8.0.5");
            var references = new List<MetadataReference>(ReferenceScopes.Runtime(_install, 8));
            foreach (var name in new[] { "RoundX", "RoundY", "RoundZ", "RoundZZ" })
            {
                var image = EmittedAssemblies.Image(name, ReferenceScopes.VERSION_ATTRIBUTE + $"namespace {name} {{ public static class Api {{ public static void Run(object value) {{ }} }} }}");
                var path = ReferenceScopes.AddAsset(_install, name, "1.0.0", "net8.0", name, image);
                references.Add(MetadataReference.CreateFromFile(path));
            }
            var compilation = ReferenceScopes.Compile("public class App { }", references);
            GenerationCandidate Candidate(string name)
            {
                var member = compilation.GetTypeByMetadataName(name + ".Api")!.GetMembers("Run").OfType<IMethodSymbol>().Single();
                return new GenerationCandidate(compilation, member,
                    Assert.IsType<GenerationTarget>(GenerationTargets.Of(member, compilation, _install.Resolver(), CancellationToken.None)), false);
            }
            X = Candidate("RoundX");
            Y = Candidate("RoundY");
            Z = (GenerationTarget)Candidate("RoundZ").Target;
            W = (GenerationTarget)Candidate("RoundZZ").Target;
        }

        internal RunResult Run(Cell cell) => new(this, cell);
        public void Dispose() => _install.Dispose();
    }

    private sealed class RunResult : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("ch-round-cell-").FullName;
        private readonly FakeClock _clock = new();
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
        internal GenerationPass Pass { get; }
        internal Observation Observed { get; }
        internal IReadOnlyList<ReachedTarget> ScopeTargets { get; }
        internal int Rounds { get; }

        internal RunResult(Fixture fixture, Cell cell)
        {
            var y = Missing(cell.Path) ? new UnavailableGenerationTarget(fixture.Y.Target.MemberId, fixture.Y.Target.LookupId, fixture.Y.Target.Key, "not installed")
                                       : fixture.Y.Target;
            var w = cell.Second == Second.Unavailable ? new UnavailableGenerationTarget(fixture.W.MemberId, fixture.W.LookupId, fixture.W.Key, "not installed")
                                                     : (IdentifiedGenerationTarget)fixture.W;
            if (GenerationRoundMatrixTests.Stored(cell.Path))
                Write(y, cell.Path == PathOfY.StoredRefusal ? Refusal(((GenerationTarget)fixture.Y.Target).Request) : Model(((GenerationTarget)fixture.Y.Target).Request));

            GeneratedAnswer Generate(GenerationRequest request, DependencyAnswers source, CancellationToken token)
            {
                _calls[request.MemberId] = _calls.GetValueOrDefault(request.MemberId) + 1;
                var views = new List<DependencyView>();
                void See(IdentifiedGenerationTarget target)
                {
                    var entry = source(target.MemberId, target.Key);
                    views.Add(new DependencyView(target, entry?.Model is not null ? DependencyViewKind.Model : entry?.Refusal is not null ? DependencyViewKind.Refusal :
                        target is UnavailableGenerationTarget ? DependencyViewKind.NoImplementation : DependencyViewKind.NoAnswer, entry));
                }
                GeneratedAnswer answer;
                if (request.MemberId == fixture.X.Target.MemberId)
                {
                    See(y);
                    if (cell.Second != Second.None)
                        See(w);
                    if (cell.Outcome == Outcome.Timeout || _calls[request.MemberId] > 1 && (cell.Path == PathOfY.TimeoutX || cell.Budget == Budget.DuringRetry))
                        return RunUntilCut(token);
                    if (_calls[request.MemberId] > 1 && cell.Path == PathOfY.FailedX)
                        throw new InvalidOperationException("repeat failed");
                    if (_calls[request.MemberId] == 1 && (cell.Path == PathOfY.NoThenBudget || cell.Chain == Chain.ZBudget || cell.Second == Second.Budget))
                        _clock.Advance(TimeSpan.FromSeconds(890));
                    var model = cell.Path == PathOfY.SameX ? Model(request) : Model(request,
                        views[0].Entry?.Model?.Effects.Select(effect => effect with { Parameter = "value" }).ToArray() ?? []);
                    answer = cell.Outcome == Outcome.Refusal ? Refusal(request) : model;
                }
                else if (request.MemberId == y.MemberId)
                {
                    if (cell.Path == PathOfY.NoThenBudget)
                        return RunUntilCut(token);
                    if (cell.Path == PathOfY.NoThenFailed)
                        throw new InvalidOperationException("dependency failed");
                    if (cell.Chain == Chain.Cycle)
                        See(fixture.X.Target);
                    else if (cell.Chain != Chain.Direct)
                        See(fixture.Z);
                    var second = _calls[request.MemberId] > 1;
                    answer = cell.Path == PathOfY.NoThenRefusal || cell.Path == PathOfY.RefusalBecomesModel && !second || cell.Path == PathOfY.ModelRefused && second
                        ? Refusal(request) : Model(request, [second ? LibraryEffect.WriteOf("value") : LibraryEffect.DeepReadOf("value")]);
                    if (cell.Budget != Budget.Enough)
                        _clock.Advance(RunGeneration.RUN_LIMIT - (cell.Budget == Budget.DuringRetry ? TimeSpan.FromSeconds(10) : TimeSpan.Zero));
                }
                else if (request.MemberId == fixture.Z.MemberId)
                    answer = cell.Chain == Chain.ZBudget ? RunUntilCut(token) : cell.Chain == Chain.ZRefusal ? Refusal(request) : Model(request);
                else if (request.MemberId == fixture.W.MemberId)
                    answer = cell.Second == Second.Budget ? RunUntilCut(token) : Refusal(request);
                else
                    throw new InvalidOperationException(request.MemberId);
                return answer with { DependencyViews = views };
            }

            var setup = new GenerationSetup(ModelGeneration.Generate, Generator: Generate, Time: _clock);
            Pass = GenerationPass.Create(setup, _root, CancellationToken.None)!;
            if (cell.Timing == Timing.Before && !GenerationRoundMatrixTests.Stored(cell.Path) && !Missing(cell.Path))
            {
                _ = Pass.Prepare(LibraryModels.BuiltIn, [fixture.Y]);
            }
            ScopeLayer layer;
            do
            {
                Rounds++;
                Assert.True(Rounds < 12, cell.ToString());
                layer = Pass.Prepare(LibraryModels.BuiltIn, [fixture.X]);
            }
            while (Pass.HasWork);

            var stored = layer.Table.Lookup(fixture.X.Compilation, fixture.X.Member, fixture.X.Target.LookupId) ?? StoredGeneratedAnswer.Absent;
            ScopeTargets = Pass.Reached(LibraryModels.BuiltIn, layer, [fixture.X]);
            var generated = Pass.GeneratedInRun([fixture.X]);
            Observed = new(_calls.GetValueOrDefault(fixture.X.Target.MemberId), _calls.GetValueOrDefault(y.MemberId), Stored(fixture.X.Target) is not null,
                Stored(fixture.X.Target) is not null, stored.Kind == StoredGeneratedKind.Model, Stored(y) is not null,
                Stored(fixture.Z) is not null, Stored(w) is not null, generated);
        }

        internal IReadOnlyList<WantedGeneration> ReadOnlyWanted(GenerationCandidate candidate)
        {
            using var pass = GenerationPass.Create(new GenerationSetup(ModelGeneration.ReadOnly,
                Generator: (_, _, _) => throw new InvalidOperationException("read-only run generated a dependency")), _root, CancellationToken.None)!;
            _ = pass.Prepare(LibraryModels.BuiltIn, [candidate]);
            return pass.Wanted(LibraryModels.BuiltIn, [candidate]);
        }

        internal GeneratedModelEntry? Stored(IdentifiedGenerationTarget target) => GeneratedModelFiles.Read(_root, target.Key).Entries.GetValueOrDefault(target.MemberId);

        private void Write(IdentifiedGenerationTarget target, GeneratedAnswer answer) => Assert.Empty(GeneratedModelFiles.Write(GeneratedModelFiles.Read(_root, target.Key),
            [answer.Model is { } model ? GeneratedModelAnswer.OfModel(model) : GeneratedModelAnswer.OfRefusal(target.MemberId,
                new GeneratedRefusal(answer.Reason!, "fixture", []))], CancellationToken.None));

        private GeneratedAnswer RunUntilCut(CancellationToken token)
        {
            for (var seconds = 0; seconds < 1000; seconds++)
            {
                token.ThrowIfCancellationRequested();
                _clock.Advance(TimeSpan.FromSeconds(1));
            }
            throw new InvalidOperationException("The member was never cut.");
        }

        public void Dispose() { Pass.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private static GeneratedAnswer Model(GenerationRequest request, IReadOnlyList<LibraryEffect>? effects = null) =>
        new(4, request.MemberId, new GenerationAssembly(request.AssemblyName, request.Version, request.PackageId), null, null,
            new LibraryModel(request.MemberId, [new SupportedAssemblyVersion(request.AssemblyName, new Version(1, 0, 0, 0), new Version(1, 0, 0, 1))], effects ?? []), null, Record());

    private static GeneratedAnswer Refusal(GenerationRequest request) =>
        new(4, request.MemberId, new GenerationAssembly(request.AssemblyName, request.Version, request.PackageId), null, GenerationReasons.NOT_A_CANDIDATE, null, null, Record());

    private static GenerationRecord Record() => new(null, null, [], 0, [], new Dictionary<string, string>(), [], 0, [], new Dictionary<string, IReadOnlyList<string>>(), 0, 0);
}
