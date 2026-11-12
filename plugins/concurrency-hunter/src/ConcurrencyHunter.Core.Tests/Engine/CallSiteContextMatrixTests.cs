using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Reporting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class CallSiteContextMatrixTests
{
    public enum Place { TwoSitesTwoRoots, TwoSitesOneRoot, OneSiteTwoRoots }
    public enum Route { InEntry, StaticChain, Instance, Constructor, Dispatch, Generic, LocalFunction, LibraryDelegate, Work, Timer, ModelRun, Receiverless }
    public enum Created
    {
        Object, Closure, Delegate, Task, Tail, Iterator, Sequence, Grouping, CompletionArray, ModelResultTask, ModelInputNew,
        ModelResultNew, ModelOutput, ModelCollection, Scope, ScopeProvider, Services, StructCopy, Storage
    }
    public enum Merge { None, ContextLimit, OpenSubstitution, SccBudget }
    public sealed record Cell(Place Place, Route Route, Created Created, Merge Merge);

    private const string LIBRARY = "SiteLibrary";
    private const string ARGUMENTS = "object marker, IServiceScopeFactory scopes, IServiceProvider provider";
    private const string INPUTS = "marker, scopes, provider";
    private static readonly IReadOnlyDictionary<Cell, string> Wider = new Dictionary<Cell, string>();

    public static IEnumerable<Cell> AllCells() =>
        from place in Enum.GetValues<Place>()
        from route in Enum.GetValues<Route>()
        from created in Enum.GetValues<Created>()
        from merge in Enum.GetValues<Merge>()
        where merge == Merge.None ? created == Created.Object || route == Route.InEntry
                                  : route == Route.StaticChain && created == Created.Object
        select new Cell(place, route, created, merge);

    public static TheoryData<Place, Route, Created, Merge> Cells()
    {
        var cells = new TheoryData<Place, Route, Created, Merge>();
        foreach (var cell in AllCells())
            cells.Add(cell.Place, cell.Route, cell.Created, cell.Merge);
        return cells;
    }

    [Fact]
    public void There_are_99_cells_covering_every_value_of_every_axis()
    {
        var cells = AllCells().ToArray();
        Assert.Equal(99, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.All(Enum.GetValues<Place>(), value => Assert.Contains(cells, cell => cell.Place == value));
        Assert.All(Enum.GetValues<Route>(), value => Assert.Contains(cells, cell => cell.Route == value));
        Assert.All(Enum.GetValues<Created>(), value => Assert.Contains(cells, cell => cell.Created == value));
        Assert.All(Enum.GetValues<Merge>(), value => Assert.Contains(cells, cell => cell.Merge == value));
    }

    [Fact]
    public void Every_cell_is_exact_and_the_wider_list_is_empty() => Assert.Empty(Wider);

    private static bool Expected(Cell cell) => cell.Place switch
    {
        Place.TwoSitesTwoRoots => false, // R3: different admitted sites never share a library-created object or its open group.
        Place.TwoSitesOneRoot => false, // R3: sequential calls at different admitted sites have different library-created objects too.
        Place.OneSiteTwoRoots => true, // R3: the same admitted site keeps the ordinary factory context across roots.
        _ => throw new ArgumentOutOfRangeException(nameof(cell))
    };

    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_observes_its_creation_under_each_admitted_site(Place place, Route route, Created created, Merge merge)
    {
        var cell = new Cell(place, route, created, merge);
        var fixture = Source(cell);
        var run = Run(fixture.Library, fixture.Program, fixture.Limits);
        var heap = run.Heap!;
        var entry = route switch
        {
            Route.Instance => "M:Runner.Run(",
            Route.Constructor => "M:Runner.#ctor(",
            _ => "M:Entry."
        };
        var calls = heap.Edges.Where(edge => heap.Instances[edge.CallerInstance].BodyId.StartsWith("body:Fixture:", StringComparison.Ordinal) &&
                                            heap.Instances[edge.CalleeInstance].BodyId.Contains(entry, StringComparison.Ordinal))
                             .Select(edge => (Body: heap.Instances[edge.CallerInstance].BodyId, Operation: edge.OperationId)).Distinct().ToArray();
        Assert.Equal(Expected(cell) ? 1 : 2, calls.Length);
        var regions = new List<HeapRegion[]>();
        foreach (var (body, operation) in calls)
        {
            var site = Site(body, operation);
            var creators = heap.Instances.Values.Where(instance => fixture.Creates(instance.BodyId) && instance.Context.EndsWith(site, StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(creators);
            if (route == Route.Timer)
                Assert.All(creators, creator => Assert.Contains(heap.TimerCallbacks, timer => timer.Callees.Contains(creator.Id)));
            var observed = heap.Regions.Values.Where(region => creators.Any(creator => CreatedBy(region, creator, created))).ToArray();
            Assert.NotEmpty(observed);
            Assert.All(observed, region =>
            {
                Assert.Contains(site, region.Identity, StringComparison.Ordinal);
                Assert.Contains(site, region.Context, StringComparison.Ordinal);
                Assert.Contains(site, region.Group, StringComparison.Ordinal);
                Assert.DoesNotContain("|site:", region.Display, StringComparison.Ordinal);
                Assert.All(calls, call => Assert.DoesNotContain(call.Body, region.Display, StringComparison.Ordinal));
            });
            var first = heap.Edges.Any(edge => heap.Instances[edge.CallerInstance].BodyId == body && edge.OperationId == operation &&
                heap.Instances[edge.CalleeInstance].BodyId.Contains("M:Entry.Left(", StringComparison.Ordinal));
            if (merge != Merge.None)
            {
                if (first || Expected(cell))
                    Assert.Contains(true, creators.Select(creator => creator.IsMerged));
                else
                    Assert.DoesNotContain(true, creators.Select(creator => creator.IsMerged));
            }
            else
            {
                Assert.DoesNotContain(creators, creator => creator.IsMerged);
            }
            if (merge == Merge.OpenSubstitution)
                Assert.Contains(observed, region => region.IsOpen == (first || Expected(cell)));
            regions.Add(observed);
        }

        if (!Expected(cell))
        {
            Assert.Empty(regions[0].Select(region => region.Identity).Intersect(regions[1].Select(region => region.Identity), StringComparer.Ordinal));
            Assert.Empty(regions[0].Select(region => region.Group).Intersect(regions[1].Select(region => region.Group), StringComparer.Ordinal));
            if (merge != Merge.OpenSubstitution)
                Assert.Equal(Names(regions[0]), Names(regions[1]));
            else
                Assert.Empty(Names(regions[0]).Intersect(Names(regions[1]), StringComparer.Ordinal));
        }
        else
        {
            var creatorIds = heap.Instances.Values.Where(instance => fixture.Creates(instance.BodyId)).Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
            var roots = run.Roots.Where(root => root.Entry.Symbol.Contains("Worker", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, roots.Length);
            // Work and timer bodies have child executions; both root trees must still reach the same creators.
            var seen = roots.Select(root => run.Executions!.Executions.Where(execution => execution.TreeRootId == root.StableRootId)
                .SelectMany(execution => run.Executions.Visits.GetValueOrDefault(execution.Id) ?? [])
                .Where(visit => creatorIds.Contains(visit.InstanceId)).Select(visit => visit.InstanceId).ToHashSet(StringComparer.Ordinal)).ToArray();
            Assert.NotEmpty(seen[0]);
            Assert.True(seen[0].SetEquals(seen[1]));
            Assert.Single(calls);
        }

        if (created is Created.Object or Created.Closure or Created.ModelResultNew or Created.Storage)
            CheckResources(run, cell, calls, regions);
    }

    private static string[] Names(IEnumerable<HeapRegion> regions) => regions.Select(region => region.Display).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static bool CreatedBy(HeapRegion region, MethodInstance creator, Created created)
    {
        var sameContext = region.Context.Contains(creator.Context, StringComparison.Ordinal);
        if (!sameContext)
            return false;
        return created switch
        {
            Created.Object => region.SiteBodyId == creator.BodyId && region.Kind == HeapRegionKind.Allocation && region.TypeKey?.StartsWith(LIBRARY + ":Box", StringComparison.Ordinal) == true,
            Created.Closure => region.Kind == HeapRegionKind.Closure && region.SiteBodyId == creator.BodyId,
            Created.Delegate => region.Kind == HeapRegionKind.Delegate && region.SiteBodyId == creator.BodyId,
            Created.Task => region.Kind == HeapRegionKind.Task && region.SiteBodyId == creator.BodyId,
            Created.Tail => region.Kind == HeapRegionKind.Task && region.SiteBodyId == creator.BodyId && region.Identity.EndsWith("|tail", StringComparison.Ordinal),
            Created.Iterator => region.Identity.StartsWith($"iterator|{creator.Id}|", StringComparison.Ordinal),
            Created.Sequence => region.Identity.StartsWith($"sequence|{creator.BodyId}#", StringComparison.Ordinal),
            Created.Grouping => region.Identity.StartsWith($"grouping|{creator.BodyId}#", StringComparison.Ordinal),
            Created.CompletionArray => region.Identity.StartsWith($"completion|{creator.BodyId}#", StringComparison.Ordinal),
            Created.ModelResultTask => region.Kind == HeapRegionKind.Task && region.SiteBodyId == creator.BodyId && region.Identity.Contains("^returns", StringComparison.Ordinal),
            Created.ModelInputNew => region.Identity.StartsWith($"model-new|{creator.BodyId}#", StringComparison.Ordinal),
            Created.ModelResultNew => region.ModelCreationKey is not null && region.SiteBodyId == creator.BodyId && region.TypeKey == LIBRARY + ":Box",
            Created.ModelOutput => region.ModelCreationKey is not null && region.SiteBodyId == creator.BodyId,
            Created.ModelCollection => region.TypeKey?.Contains("System.Collections.Generic.List<", StringComparison.Ordinal) == true && region.SiteBodyId == creator.BodyId,
            Created.Scope => region.Identity.StartsWith($"scope|{creator.BodyId}#", StringComparison.Ordinal),
            Created.ScopeProvider => region.Identity.StartsWith($"provider|scope:scope|{creator.BodyId}#", StringComparison.Ordinal),
            Created.Services => region.Identity.StartsWith($"services|{creator.BodyId}#", StringComparison.Ordinal),
            Created.StructCopy => region.Kind == HeapRegionKind.StructCopy && region.Identity.StartsWith($"copy|{creator.BodyId}#", StringComparison.Ordinal),
            Created.Storage => region.Kind == HeapRegionKind.StructStorage && region.Holder?.StartsWith($"alloc|{creator.BodyId}#", StringComparison.Ordinal) == true,
            _ => throw new ArgumentOutOfRangeException(nameof(created))
        };
    }

    private static void CheckResources(ScopeRun run, Cell cell, (string Body, int Operation)[] calls, List<HeapRegion[]> regions)
    {
        var heap = run.Heap!;
        var all = regions.SelectMany(values => values).ToArray();
        string Resource(HeapRegion region) => region.Holder ?? region.Identity;
        var identities = all.Select(Resource).ToHashSet(StringComparer.Ordinal);
        var field = cell.Created == Created.Closure ? "captured" : cell.Created == Created.Storage ? "Nested" : "Value";
        var accesses = run.Collection!.Accesses.Where(access => access.Resource.RegionId is { } id && identities.Contains(id) &&
            access.Resource.Member.Name == field).ToArray();
        Assert.NotEmpty(accesses);
        Assert.Contains(accesses, access => access.Operation == AccessOperation.Write);
        Assert.Contains(accesses, access => access.Operation == AccessOperation.Read);
        foreach (var access in accesses)
        {
            Assert.DoesNotContain("|site:", access.Resource.RegionKey!, StringComparison.Ordinal);
            Assert.DoesNotContain("|site:", ReportRenderer.ResourceText(access.Resource), StringComparison.Ordinal);
            Assert.All(calls, call =>
            {
                Assert.DoesNotContain(call.Body, access.Resource.RegionKey!, StringComparison.Ordinal);
                Assert.DoesNotContain(call.Body, ReportRenderer.ResourceText(access.Resource), StringComparison.Ordinal);
            });
        }
        if (!Expected(cell) && cell.Merge != Merge.OpenSubstitution)
        {
            string[] Keys(HeapRegion[] values) => accesses.Where(access => values.Any(region => Resource(region) == access.Resource.RegionId))
                .Select(access => access.Resource.RegionKey!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(Keys(regions[0]));
            Assert.NotEmpty(Keys(regions[1]));
            Assert.Equal(Keys(regions[0]), Keys(regions[1]));
            string[] Texts(HeapRegion[] values) => accesses.Where(access => values.Any(region => Resource(region) == access.Resource.RegionId))
                .Select(access => ReportRenderer.ResourceText(access.Resource)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(Texts(regions[0]), Texts(regions[1]));
        }
        var markerNames = new[] { "LeftMarker", "RightMarker" };
        for (var index = 0; index < regions.Count; index++)
        {
            var expectedMarkers = cell.Place switch
            {
                Place.TwoSitesTwoRoots => new[] { calls[index].Body.Contains("LeftWorker", StringComparison.Ordinal) ? "LeftMarker" : "RightMarker" },
                Place.TwoSitesOneRoot => new[] { calls[index].Operation == calls.Min(call => call.Operation) ? "LeftMarker" : "RightMarker" },
                Place.OneSiteTwoRoots => markerNames,
                _ => throw new ArgumentOutOfRangeException(nameof(cell))
            };
            foreach (var region in regions[index])
            {
                var slot = cell.Created == Created.Closure
                    ? WholeProgram.ClosureSlot(new IrClosureCell("seen", "", "seen")) : "Seen";
                var markers = heap.PointsTo(Resource(region), slot).Select(id => heap.Regions[id].TypeKey).Where(key => key is not null && markerNames.Any(name => key.EndsWith(":" + name, StringComparison.Ordinal))).ToArray();
                Assert.NotEmpty(markers);
                Assert.Equal(expectedMarkers.Order(StringComparer.Ordinal), markers.Select(key => key![(key!.LastIndexOf(':') + 1)..])
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            }
        }
        var pairs = InterproceduralPairing.Pair(run.Collection.Accesses, run.Executions!, heap).Pairs.Where(pair =>
            pair.Resource.Member.Name == field && pair.First.Root.RootId != pair.Second.Root.RootId).ToArray();
        Assert.Equal(Expected(cell), pairs.Length != 0);
    }

    [Fact]
    public void Objects_library_bodies_create_under_two_admitted_sites_in_two_roots_make_no_pair()
    {
        foreach (var shared in new[] { false, true })
        {
            var action = $"var value = {(shared ? "Shared.Make()" : "Entry.Make()")}; Published = value; value.Value = marker; _ = value.Value;";
            var program = Workers(Place.TwoSitesTwoRoots, action, action) + "public static class Shared { public static Box Make() => Entry.Make(); }";
            var run = Run("public static class Entry { public static Box Make() => Factory.Create(); } public static class Factory { public static Box Create() => new Box(); }", program);
            Assert.Contains(run.Collection!.Accesses, access => access.Resource.Member.Name == "Value");
            Assert.Equal(shared, CrossPairs(run, "Value"));
        }
    }

    [Fact]
    public void Library_static_written_under_two_admitted_sites_keeps_its_pair()
    {
        var run = Run("public static class Entry { public static object? Value; public static void Write(object marker) { Value = marker; } }",
            Workers(Place.TwoSitesTwoRoots, "Entry.Write(marker);", "Entry.Write(marker);"));
        Assert.True(CrossPairs(run, "Value"));
        Assert.Single(run.Heap!.Regions.Values, region => region.Kind == HeapRegionKind.Static && region.TypeKey == LIBRARY + ":Entry");
    }

    [Fact]
    public void Caller_delegate_a_library_object_holds_runs_only_under_its_own_root()
    {
        const string LIB = """
            public sealed class Held { public Action Action = null!; public void Run() => Action(); }
            public static class Entry { public static Held Make(Action action) => new Held { Action = action }; }
            """;
        foreach (var shared in new[] { false, true })
        {
            var action = shared ? "var held = Shared.Make(() => Hits++); held.Run();" : "var held = Entry.Make(() => Hits++); held.Run();";
            var program = Workers(Place.TwoSitesTwoRoots, action, action) + "public static class Shared { public static Held Make(Action action) => Entry.Make(action); }";
            var run = Run(LIB, program);
            Assert.Contains(run.Collection!.Accesses, access => access.Resource.Member.Name == "Hits");
            var hits = run.Collection.Accesses.Where(access => access.Resource.Member.Name == "Hits").ToArray();
            foreach (var worker in new[] { "LeftWorker", "RightWorker" })
            {
                Assert.Contains(hits, access => access.BodyId!.Contains(worker, StringComparison.Ordinal) && access.Root.RootId.Contains(worker, StringComparison.Ordinal));
                if (!shared)
                    Assert.DoesNotContain(hits, access => access.BodyId!.Contains(worker, StringComparison.Ordinal) && !access.Root.RootId.Contains(worker, StringComparison.Ordinal));
            }
            Assert.Equal(shared, CrossPairs(run, "Hits"));
        }
    }

    [Fact]
    public void Program_body_contexts_do_not_change_with_admission()
    {
        var program = Workers(Place.TwoSitesTwoRoots, "Own.Make().Value = marker; Entry.Make();", "Own.Make().Value = marker; Entry.Make();") +
            "public static class Own { public static Box Make() => new Box(); }";
        var library = "public static class Entry { public static Box Make() => new Box(); }";
        var before = Run(library, program, admitted: false);
        var after = Run(library, program);
        Assert.Equal(ProgramInstances(before), ProgramInstances(after));
        Assert.Equal(ProgramRegions(before), ProgramRegions(after));
        static string[] ProgramInstances(ScopeRun run) => run.Heap!.Instances.Values.Where(instance => instance.BodyId.StartsWith("body:Fixture:", StringComparison.Ordinal))
            .Select(instance => $"{instance.Id}|{instance.Context}|{instance.IsMerged}").Order(StringComparer.Ordinal).ToArray();
        static string[] ProgramRegions(ScopeRun run) => run.Heap!.Regions.Values.Where(region => region.SiteBodyId?.StartsWith("body:Fixture:", StringComparison.Ordinal) == true)
            .Select(region => $"{region.Identity}|{region.Context}|{region.Group}|{region.Display}").Order(StringComparer.Ordinal).ToArray();
    }

    private static bool CrossPairs(ScopeRun run, string field) => InterproceduralPairing.Pair(run.Collection!.Accesses, run.Executions!, run.Heap!).Pairs
        .Any(pair => pair.Resource.Member.Name == field && pair.First.Root.RootId != pair.Second.Root.RootId);

    private sealed record Fixture(string Library, string Program, AnalysisLimits Limits, Func<string, bool> Creates);

    private static Fixture Source(Cell cell)
    {
        var code = Creation(cell.Created);
        var creates = "M:Entry.Left(";
        string first;
        string second;
        string library;
        var limits = AnalysisLimits.Default;
        if (cell.Merge != Merge.None)
        {
            creates = "M:Maker.Create";
            if (cell.Merge == Merge.ContextLimit)
            {
                limits = limits with { MaxContextsPerMethod = 2 };
                first = $"Maker.Create({INPUTS}); Maker.Create({INPUTS}); Maker.Create({INPUTS});";
                second = $"Maker.Create({INPUTS});";
                library = $"public static class Maker {{ public static void Create({ARGUMENTS}) {{ {code} }} }}";
            }
            else if (cell.Merge == Merge.OpenSubstitution)
            {
                limits = limits with { MaxContextsPerMethod = 2 };
                first = $"Open<byte>({INPUTS}); Open<short>({INPUTS}); Open<long>({INPUTS});";
                second = $"Maker.Create<int>({INPUTS});";
                library = $"public static class Maker {{ public static void Create<T>({ARGUMENTS}) {{ var value = new Box<T>(); Touch(value, marker); }} }}";
            }
            else
            {
                limits = limits with { MaxSccIterations = 1 };
                first = $"Maker.Create({INPUTS}, Maker.Odd);";
                second = $"Maker.Create({INPUTS}, null);";
                library = $$"""
                    public static class Maker
                    {
                        public static object Create({{ARGUMENTS}}, Func<object, object>? again)
                        {
                            if (again != null) marker = again(marker);
                            {{code}}
                            return marker;
                        }
                        public static object Odd(object marker)
                        {
                            if (Environment.TickCount > 0) return Create(marker, null!, null!, Odd);
                            return marker;
                        }
                    }
                    """;
            }
            library += $"public static class Entry {{ public static void Left({ARGUMENTS}) {{ {first} }} public static void Right({ARGUMENTS}) {{ {second} }} " +
                (cell.Merge == Merge.OpenSubstitution ? $"private static void Open<T>({ARGUMENTS}) {{ Maker.Create<T>({INPUTS}); }}" : "") + "}";
        }
        else
        {
            string member = $"public static void Create({ARGUMENTS}) {{ {code} }}";
            (first, library, creates) = cell.Route switch
            {
                Route.InEntry => (code, "", "M:Entry.Left("),
                Route.StaticChain => ($"Maker.Create({INPUTS});", $"public static class Maker {{ {member} }}", "M:Maker.Create("),
                Route.Instance => ($"Entry.Factory().Run({INPUTS});", $"public sealed class Runner {{ public void Run({ARGUMENTS}) {{ {code} }} }}", "M:Runner.Run("),
                Route.Constructor => ($"new Runner({INPUTS});", $"public sealed class Runner {{ public Runner({ARGUMENTS}) {{ {code} }} }}", "M:Runner.#ctor("),
                Route.Dispatch => ($"Runner runner = new Actual(); runner.Run({INPUTS});", $"public abstract class Runner {{ public abstract void Run({ARGUMENTS}); }} public sealed class Actual : Runner {{ public override void Run({ARGUMENTS}) {{ {code} }} }}", "M:Actual.Run("),
                Route.Generic => ($"Maker.Create<int>({INPUTS});", $"public static class Maker {{ public static void Create<T>({ARGUMENTS}) {{ {code} }} }}", "M:Maker.Create``1("),
                Route.LocalFunction => ($"void Create() {{ {code} }} Create();", "", "#local:"),
                Route.LibraryDelegate => ($"Action create = () => {{ {code} }}; create();", "", "#lambda"),
                Route.Work => ($"Task.Run(() => {{ {code} }});", "", "#lambda"),
                Route.Timer => ($"new Timer(_ => {{ {code} }}, null, 0, 1000);", "", "#lambda"),
                Route.ModelRun => ($"Models.Now(() => {{ {code} }});", "", "#lambda"),
                Route.Receiverless => ($"Runner? runner = null; runner!.Run({INPUTS});", $"public sealed class Runner {{ public void Run({ARGUMENTS}) {{ {code} }} }}", "M:Runner.Run("),
                _ => throw new ArgumentOutOfRangeException(nameof(cell))
            };
            // One creation body is reached under both project sites, even for a creation inside the admitted entry.
            var async = cell.Created == Created.CompletionArray ? "async Task" : "void";
            library += $"public static class Entry {{ public static {async} Left({ARGUMENTS}) {{ {first} }} " +
                (cell.Route == Route.Instance ? "public static Runner Factory() => new Runner();" : "") + "}";
        }
        var awaitCall = cell.Created == Created.CompletionArray ? "await " : "";
        first = $"{awaitCall}Entry.Left({INPUTS});";
        second = $"{awaitCall}Entry.{(cell.Merge == Merge.None ? "Left" : "Right")}({INPUTS});";
        if (cell.Route == Route.Instance)
            first = second = $"Entry.Factory().Run({INPUTS});";
        else if (cell.Route == Route.Constructor)
            first = second = $"new Runner({INPUTS});";
        return new Fixture(library, Workers(cell.Place, first, second), limits,
            body => body.StartsWith("body:" + LIBRARY + ":", StringComparison.Ordinal) && body.Contains(creates, StringComparison.Ordinal) &&
                (creates.StartsWith('#') || !body.Contains("#lambda", StringComparison.Ordinal) && !body.Contains("#local:", StringComparison.Ordinal)));
    }

    private static string Creation(Created created) => created switch
    {
        Created.Object => "var value = new Box(); Touch(value, marker);",
        Created.Closure => "object? seen = null; object captured = marker; Action update = () => { captured = marker; seen = captured; }; update(); Kept = update;",
        Created.Delegate => "Action value = () => { Kept = marker; }; Kept = value;",
        Created.Task => "var value = Idle(); Kept = value;",
        Created.Tail => "var value = Task.Factory.StartNew(async () => { await Task.Yield(); }); Kept = value;",
        Created.Iterator => "var value = Iterate(marker); Kept = value;",
        Created.Sequence => "var value = Models.Sequence(marker); Kept = value;",
        Created.Grouping => "var sequence = new[] { marker }.GroupBy(item => item); foreach (var value in sequence) { Kept = value; }",
        Created.CompletionArray => "var value = await Task.WhenAll(Task.FromResult(marker)); Kept = value;",
        Created.ModelResultTask => "var value = Models.Async(async () => { await Task.Yield(); return marker; }); Kept = value;",
        Created.ModelInputNew => "Models.Input<Box>(value => { Kept = value; });",
        Created.ModelResultNew => "var value = JsonSerializer.Deserialize<Box>(\"{}\")!; Touch(value, marker);",
        Created.ModelOutput => "Models.Collect(marker, out var value); Kept = value;",
        Created.ModelCollection => "var value = new[] { marker }.ToList(); Kept = value;",
        Created.Scope => "var value = scopes.CreateScope(); Kept = value;",
        Created.ScopeProvider => "var scope = scopes.CreateScope(); var value = scope.ServiceProvider; Kept = value;",
        Created.Services => "var scope = scopes.CreateScope(); var value = scope.ServiceProvider.GetServices<Service>(); Kept = value;",
        Created.StructCopy => "Cell first = new Cell { Value = marker }; Cell value = first; Kept = value.Value;",
        Created.Storage => "var value = new Box(); Kept = value; value.Nested.Value = marker; value.Seen = value.Nested.Value;",
        _ => throw new ArgumentOutOfRangeException(nameof(created))
    };

    private static string Workers(Place place, string first, string second)
    {
        var shared = place == Place.OneSiteTwoRoots;
        var one = place == Place.TwoSitesOneRoot;
        var helper = shared ? $"public sealed class Shared {{ public async Task Run({ARGUMENTS}) {{ {first} }} }}" : "";
        string Worker(string name, string work, string marker) => $$"""
            public sealed class {{name}}(IServiceScopeFactory scopes, IServiceProvider provider{{(shared ? ", Shared shared" : "")}}) : BackgroundService
            {
                public int Hits;
                public static object? Published;
                protected override async Task ExecuteAsync(CancellationToken token)
                {
                    object marker = new {{marker}}();
                    {{work}}
                }
            }
            """;
        return Usings + "public sealed class LeftMarker { } public sealed class RightMarker { }" + helper +
            Worker("LeftWorker", shared ? $"await shared.Run({INPUTS});" : first + (one ? "marker = new RightMarker(); " + second : ""), "LeftMarker") +
            (one ? "" : Worker("RightWorker", shared ? $"await shared.Run({INPUTS});" : second, "RightMarker")) +
            Startup("services.AddScoped<Service>(); " + (shared ? "services.AddSingleton<Shared>(); " : "") +
                "services.AddHostedService<LeftWorker>();" + (one ? "" : "services.AddHostedService<RightWorker>();"));
    }

    private static readonly Lazy<(CSharpCompilation Compilation, MetadataReference Reference)> ModelLibrary = new(() => Compile("ContextModels", """
        using System;
        using System.Threading.Tasks;
        using System.Collections.Generic;
        public static class Models
        {
            public static void Now(Action action) { }
            public static void Input<T>(Action<T> action) where T : class { }
            public static IEnumerable<T> Sequence<T>(T item) => null!;
            public static Task<T> Async<T>(Func<Task<T>> action) => null!;
            public static void Collect<T>(T item, out List<T> value) { value = null!; }
        }
        """));

    private static LibraryModels Models()
    {
        string Entry(string method, string properties) => "{\"member\":\"" + DocumentationCommentId.CreateDeclarationId(
            ModelLibrary.Value.Compilation.GetTypeByMetadataName("Models")!.GetMembers(method).OfType<IMethodSymbol>().Single()) + "\",\"effects\":{}," + properties + "}";
        var entries = new[]
        {
            Entry("Now", "\"fates\":{\"action\":{\"fate\":\"invoke-now\",\"inputs\":[]}}"),
            Entry("Input", "\"fates\":{\"action\":{\"fate\":\"invoke-now\",\"inputs\":[[\"new\"]]}}"),
            Entry("Sequence", "\"result\":\"sequence(arg:item)\""),
            Entry("Async", "\"result\":\"[returns:action]\",\"fates\":{\"action\":{\"fate\":\"invoke-now\",\"inputs\":[]}}"),
            Entry("Collect", "\"outputs\":{\"value\":\"collection(arg:item)\"}")
        };
        var table = BuiltInModelReader.Read(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"assemblies\":[\"ContextModels\"],\"versions\":{\"minimum\":\"0.0.0.0\",\"maximumExclusive\":\"1.0.0.0\"},\"models\":[" + string.Join(",", entries) + "]}"));
        return new LibraryModels([.. LibraryModels.BuiltIn.Members, .. table.Members], LibraryModels.BuiltIn.ImmutableTypes);
    }

    private static ScopeRun Run(string libraryText, string programText, AnalysisLimits? limits = null, bool admitted = true)
    {
        var (library, reference) = Compile(LIBRARY, LIBRARY_USINGS + COMMON + libraryText, ModelLibrary.Value.Reference);
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [reference, ModelLibrary.Value.Reference] }, ("Case.cs", programText));
        var project = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        Assert.Empty(project.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var entries = ConcurrencyHunter.Frontend.ProgramIndexBuilder.Build("sites", [library], ROOT_DIRECTORY, CancellationToken.None).Methods
            .Where(method => method.ContainingTypeKey == LIBRARY + ":Entry" || method.ContainingTypeKey == LIBRARY + ":Held" ||
                             method.ContainingTypeKey == LIBRARY + ":Runner")
            .Select(method => method.MethodId).ToHashSet(StringComparer.Ordinal);
        var run = ScopePipeline.Run("scope:Fixture", [project], [(project, null)], ROOT_DIRECTORY, ProviderRegistry.BuiltIn, Models(),
            limits ?? AnalysisLimits.Default, new(), null, CancellationToken.None,
            reachability: ReachabilityRules.ClassHierarchy with { Admission = admitted ? new CallSiteAdmission(LIBRARY, entries) : null },
            libraryCompilations: admitted ? [library] : []);
        Assert.False(run.Stopped);
        Assert.NotNull(run.Heap);
        return run;
    }

    private static (CSharpCompilation, MetadataReference) Compile(string name, string source, params MetadataReference[] references)
    {
        var compilation = ((CSharpCompilation)FixtureSolution.Create(new FixtureOptions { MetadataReferences = references }, ("Library.cs", source))
            .Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!).WithAssemblyName(name);
        using var pe = new MemoryStream();
        var emitted = compilation.Emit(pe);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return (compilation, MetadataReference.CreateFromImage(pe.ToArray()));
    }

    private static string Site(string body, int operation) => "|site:" + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{body}#{operation}")) + ";";

    private const string LIBRARY_USINGS = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using System.Collections.Generic;
        using System.Linq;
        using System.Text.Json;
        using Microsoft.Extensions.DependencyInjection;
        using static Helpers;
        """;
    private const string COMMON = """
        public struct Cell { public object? Value; }
        public class Box { public object? Value; public object? Seen; public Cell Nested; }
        public sealed class Box<T> : Box { }
        public sealed class Service { }
        public static class Helpers
        {
            public static object? Kept;
            public static async Task Idle() { await Task.Yield(); }
            public static IEnumerable<object> Iterate(object marker) { yield return marker; }
            public static void Touch(Box value, object marker) { Kept = value; value.Value = marker; value.Seen = value.Value; }
        }
        """;
}
