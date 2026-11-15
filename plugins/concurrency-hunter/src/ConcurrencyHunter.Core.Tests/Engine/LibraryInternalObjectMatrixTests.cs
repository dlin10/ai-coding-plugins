using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using Xunit;
using Xunit.Sdk;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class LibraryInternalObjectMatrixTests
{
    private enum Creator { Library, Program }
    private enum Holder { LibraryStatic, LibraryObject, InsideHeld, ProgramLocal, ProgramField, ProgramDelegate, ProgramObjectField, ProgramCollection, LibraryStruct, ProgramStruct }
    private enum Accessors { LibraryLibrary, LibraryProgram, ProgramProgram }
    private enum Operation { WriteWrite, WriteRead, ReadModifyWrite, UnknownEffect }
    private enum Storage { Field, StructField, CollectionStructure, CollectionCell, ArrayCellByReference }
    private enum Merge { None, ContextLimit, OpenGroup }
    private enum Earlier { None, ReadRead, Protected }
    private enum Closure { None, ClosureKeptByLibrary, ClosureDelegateHeldByProgram }
    private sealed record Cell(Creator Creator, Holder Holder, Accessors Accessors, Operation Operation = Operation.WriteWrite,
                               Storage Storage = Storage.Field, Merge Merge = Merge.None, Earlier Earlier = Earlier.None, Closure Closure = Closure.None);
    private sealed record Source(string Library, string Program, AnalysisLimits Limits);
    private sealed record Observation(Cell Cell, ScopeRun Run, Access[] Accesses, PairAnalysis Before, PairAnalysis After,
                                      PairAnalysis CrossBefore, PairAnalysis CrossAfter);

    private static readonly Lazy<Observation[]> Observations = new(() => Cells().Select(Observe).ToArray());

    private static IEnumerable<Cell> Cells()
    {
        var cells = new HashSet<Cell>();
        foreach (var creator in Enum.GetValues<Creator>())
        foreach (var holder in Enum.GetValues<Holder>())
        foreach (var accessors in Enum.GetValues<Accessors>())
        {
            var cell = new Cell(creator, holder, accessors);
            if (Allowed(cell)) cells.Add(cell);
        }
        foreach (var holder in new[] { Holder.LibraryObject, Holder.InsideHeld, Holder.ProgramField })
        {
            var cell = new Cell(Creator.Library, holder, Accessors.LibraryLibrary);
            foreach (var operation in Enum.GetValues<Operation>()) cells.Add(cell with { Operation = operation });
            foreach (var storage in Enum.GetValues<Storage>()) cells.Add(cell with { Storage = storage });
            foreach (var merge in Enum.GetValues<Merge>()) cells.Add(cell with { Merge = merge });
            foreach (var earlier in Enum.GetValues<Earlier>()) cells.Add(cell with { Earlier = earlier });
        }
        cells.Add(new(Creator.Library, Holder.LibraryStatic, Accessors.LibraryLibrary, Closure: Closure.ClosureKeptByLibrary));
        cells.Add(new(Creator.Library, Holder.ProgramField, Accessors.LibraryLibrary, Closure: Closure.ClosureDelegateHeldByProgram));
        return cells;
    }

    private static bool Allowed(Cell cell)
    {
        // A program-created X is already the program's; these three holders show that library holders/accessors cannot change that.
        if (cell.Creator == Creator.Program && cell.Holder is not (Holder.LibraryStatic or Holder.LibraryObject or Holder.ProgramField))
            return false;
        // Program access requires a direct reference to X. Returning X would be the ProgramLocal cell instead.
        return cell.Creator != Creator.Library || cell.Accessors == Accessors.LibraryLibrary ||
               cell.Holder is not (Holder.LibraryStatic or Holder.LibraryObject or Holder.InsideHeld or Holder.LibraryStruct);
    }

    private static bool Expected(Cell cell)
    {
        if (cell.Earlier != Earlier.None) return false; // R3: earlier decisions retain their own counts.
        if (cell.Closure != Closure.None) return true; // R1, ADR 0037: holding the delegate is not holding its separate closure.
        if (cell.Creator != Creator.Library || cell.Accessors != Accessors.LibraryLibrary || cell.Merge != Merge.None)
            return false; // R2: program creation/access and unknown holders retain their pairs.
        // R1: only direct program references count; a program-held struct carries such a reference as well.
        return cell.Holder is Holder.LibraryStatic or Holder.LibraryObject or Holder.InsideHeld or Holder.LibraryStruct;
    }

    [Fact]
    public void Every_cell_is_exact() => Each(observation =>
    {
        var expected = Expected(observation.Cell);
        Assert.Equal(expected ? 0 : observation.CrossBefore.Pairs.Count, observation.CrossAfter.Pairs.Count);
        Assert.Equal(expected ? observation.CrossBefore.Pairs.Count : 0,
                     observation.CrossAfter.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_LIBRARY_INTERNAL));
        if (!expected) Assert.Equal(PairKeys(observation.CrossBefore), PairKeys(observation.CrossAfter));
    });

    [Fact]
    public void No_cell_is_skipped_where_its_expectation_pairs() => Each(observation =>
    {
        if (Expected(observation.Cell) || observation.Cell.Earlier != Earlier.None) return;
        Assert.NotEmpty(observation.CrossAfter.Pairs);
        Assert.Equal(0, observation.After.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_LIBRARY_INTERNAL));
    });

    [Fact]
    public void Existing_counts_are_those_of_the_pairing_before_the_rule() => Each(observation =>
    {
        SameExistingCounts(observation.Before, observation.After);
        SameExistingCounts(observation.CrossBefore, observation.CrossAfter);
    });

    [Fact]
    public void Every_value_of_every_axis_occurs_and_the_cells_are_counted()
    {
        var cells = Cells().ToArray();
        Assert.Equal(31 + 3 * (3 + 4 + 2 + 2) + 2, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Covers(cells.Select(cell => cell.Creator));
        Covers(cells.Select(cell => cell.Holder));
        Covers(cells.Select(cell => cell.Accessors));
        Covers(cells.Select(cell => cell.Operation));
        Covers(cells.Select(cell => cell.Storage));
        Covers(cells.Select(cell => cell.Merge));
        Covers(cells.Select(cell => cell.Earlier));
        Covers(cells.Select(cell => cell.Closure));
        static void Covers<T>(IEnumerable<T> values) where T : struct, Enum =>
            Assert.Equal(Enum.GetValues<T>().Order(), values.Distinct().Order());
    }

    [Fact]
    public void The_inside_of_a_held_library_object_is_the_accepted_narrowing() => Each(observation =>
    {
        // ADR 0037, Consequences: a race only on an inside object is the accepted narrowing of the direct-holder rule.
        if (observation.Cell is not { Holder: Holder.InsideHeld, Merge: Merge.None, Earlier: Earlier.None }) return;
        Assert.True(Expected(observation.Cell));
        Assert.NotEmpty(observation.CrossBefore.Pairs);
        Assert.Empty(observation.CrossAfter.Pairs);
    });

    [Fact]
    public void Skips_and_pairs_do_not_depend_on_the_candidate_index() => Each(observation =>
    {
        var run = observation.Run;
        SamePairing(observation.After, ReferencePair(observation.Accesses, run.Executions!, run.Heap!));
        SamePairing(observation.Before, ReferencePair(observation.Accesses.Select(access => access with { IsLibraryInternal = false }).ToArray(),
                                                     run.Executions!, run.Heap!));
    });

    private static void Each(Action<Observation> check)
    {
        foreach (var observation in Observations.Value)
        {
            try { check(observation); }
            catch (Exception error) { throw new XunitException($"{observation.Cell}: {error}"); }
        }
    }

    private static Observation Observe(Cell cell)
    {
        try
        {
            var source = Fixture(cell);
            var run = CallSiteContextMatrixTests.Run(source.Library, source.Program, source.Limits);
            var heap = run.Heap!;
            var roots = run.Roots.Where(root => root.Entry.Symbol.Contains("Worker", StringComparison.Ordinal)).Select(root => root.StableRootId).ToHashSet();
            Assert.Equal(2, roots.Count);
            Assert.Single(heap.Edges.Where(edge => heap.Instances[edge.CallerInstance].BodyId.StartsWith("body:Fixture:M:Shared.Run", StringComparison.Ordinal) &&
                                                   heap.Instances[edge.CalleeInstance].BodyId.StartsWith("body:SiteLibrary:M:Entry.Visit", StringComparison.Ordinal))
                                    .Select(edge => new { heap.Instances[edge.CallerInstance].BodyId, edge.OperationId }).Distinct());
            var accesses = run.Collection!.Accesses.Where(access => !access.IsConstructionLocal && roots.Contains(access.Root.RootId) && TouchesX(access, cell, heap)).ToArray();
            Assert.NotEmpty(accesses);
            Assert.Equal(2, accesses.Select(access => access.Root.RootId).Distinct().Count());
            Premises(cell, run, accesses);
            var before = accesses.Select(access => access with { IsLibraryInternal = false }).ToArray();
            var crossBefore = Cross(before, run);
            if (cell.Earlier == Earlier.None)
            {
                Assert.NotEmpty(crossBefore.Pairs);
                // WriteRead also observes the two reads meeting; only those nonconflicting pairs may have an earlier reason.
                Assert.All(crossBefore.Skips, skip => Assert.True(cell.Operation == Operation.WriteRead && skip.Key == InterproceduralPairing.SKIP_READ_READ));
                Assert.Equal(0, crossBefore.Suppressed);
            }
            else
            {
                Assert.Empty(crossBefore.Pairs);
                if (cell.Earlier == Earlier.ReadRead)
                    Assert.Equal(crossBefore.Comparisons, crossBefore.Skips[InterproceduralPairing.SKIP_READ_READ]);
                else
                {
                    Assert.Empty(crossBefore.Skips);
                    Assert.Equal(crossBefore.Comparisons, crossBefore.Suppressed);
                }
            }
            return new(cell, run, accesses, InterproceduralPairing.Pair(before, run.Executions!, heap),
                       InterproceduralPairing.Pair(accesses, run.Executions!, heap), crossBefore, Cross(accesses, run));
        }
        catch (Exception error) { throw new XunitException($"Premise of {cell}: {error}"); }
    }

    private static bool TouchesX(Access access, Cell cell, HeapSolution heap)
    {
        var id = access.Resource.CollectionId ?? access.Resource.RegionId;
        if (id is null || !heap.Regions.TryGetValue(id, out var region)) return false;
        if (cell.Closure != Closure.None) return region.Kind == HeapRegionKind.Closure && access.Resource.Member.Name == "captured";
        if (cell.Storage is Storage.CollectionStructure or Storage.CollectionCell)
            return access.Resource.CollectionId is not null && region.TypeKey?.Contains("List<", StringComparison.Ordinal) == true;
        if (cell.Storage == Storage.ArrayCellByReference)
            return region.TypeKey?.Contains("int[]", StringComparison.Ordinal) == true && access.IsReferenceAccess;
        return region.TypeKey?.StartsWith("SiteLibrary:Item", StringComparison.Ordinal) == true &&
               (access.Resource.IsWildcard || access.Resource.Member.Name == (cell.Storage == Storage.StructField ? "Nested" : "Hits"));
    }

    private static void Premises(Cell cell, ScopeRun run, Access[] accesses)
    {
        var heap = run.Heap!;
        var ids = accesses.Select(access => access.Resource.CollectionId ?? access.Resource.RegionId!).Distinct().ToArray();
        Assert.All(ids, id =>
        {
            var region = heap.Regions[id];
            Assert.StartsWith(cell.Creator == Creator.Library ? "body:SiteLibrary:" : "body:Fixture:", region.SiteBodyId!);
            Assert.Equal(cell.Merge == Merge.ContextLimit, region.IsMerged);
            Assert.False(region.IsOpen);
            Assert.Equal(cell.Merge == Merge.OpenGroup, heap.Regions.Values.Any(other => other.Group == region.Group && other.IsOpen));
        });
        var programValues = ProgramValues(heap);
        var programSlots = heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Di || region.SiteBodyId?.StartsWith("body:Fixture:", StringComparison.Ordinal) == true ||
                                                               region.Kind == HeapRegionKind.Static && region.TypeKey?.StartsWith("Fixture:", StringComparison.Ordinal) == true)
                               .SelectMany(region => heap.FieldsOf(region.Identity).SelectMany(slot => heap.PointsTo(region.Identity, slot))).ToHashSet();
        if (cell.Closure != Closure.None)
        {
            var delegates = heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Delegate && region.SiteBodyId?.StartsWith("body:SiteLibrary:", StringComparison.Ordinal) == true).ToArray();
            Assert.NotEmpty(delegates);
            Assert.All(ids, id =>
            {
                Assert.DoesNotContain(id, programValues);
                Assert.DoesNotContain(id, programSlots);
                Assert.Contains(delegates, target => heap.DelegateCaptures(target.Identity).Contains(id));
            });
            var holders = heap.Regions.Values.Where(region => cell.Closure == Closure.ClosureKeptByLibrary
                ? region.Kind == HeapRegionKind.Static && region.TypeKey == "SiteLibrary:Entry"
                : region.Kind == HeapRegionKind.Di && region.TypeKey == "Fixture:Shared").ToArray();
            Assert.Contains(holders, holder => heap.PointsTo(holder.Identity, "SavedDelegate").Any(id => delegates.Any(target => target.Identity == id)));
            Assert.All(delegates, target => Assert.DoesNotContain(target.Identity, ids));
            return;
        }
        var internalHolder = cell.Holder is Holder.LibraryStatic or Holder.LibraryObject or Holder.InsideHeld or Holder.LibraryStruct;
        if (cell.Creator == Creator.Library && internalHolder)
        {
            Assert.All(ids, id => Assert.DoesNotContain(id, programValues));
            Assert.All(ids, id => Assert.DoesNotContain(id, programSlots));
        }
        if (cell.Accessors == Accessors.LibraryLibrary && cell.Holder is Holder.ProgramObjectField or Holder.ProgramCollection or Holder.ProgramStruct)
            Assert.All(ids, id => Assert.DoesNotContain(id, programValues));
        switch (cell.Holder)
        {
            case Holder.LibraryStatic:
            case Holder.LibraryStruct:
                Slot("SiteLibrary:Entry", "Saved", ids);
                if (cell.Holder == Holder.LibraryStruct)
                    Assert.Contains(heap.Regions.Values, region => region.Kind == HeapRegionKind.StructCopy && heap.PointsTo(region.Identity, "Value").Overlaps(ids));
                break;
            case Holder.LibraryObject:
            case Holder.InsideHeld:
                Slot("SiteLibrary:Outer", "Value", ids);
                var outer = heap.Regions.Values.Where(region => region.TypeKey == "SiteLibrary:Outer" && region.Kind == HeapRegionKind.Allocation).ToArray();
                Assert.NotEmpty(outer);
                Assert.All(outer, region => Assert.StartsWith("body:SiteLibrary:", region.SiteBodyId!));
                if (cell.Holder == Holder.InsideHeld) Slot("Fixture:Shared", "_outer", outer.Select(region => region.Identity).ToArray());
                else Slot("SiteLibrary:Entry", "SavedOuter", outer.Select(region => region.Identity).ToArray());
                break;
            case Holder.ProgramField: Slot("Fixture:Shared", "_x", ids); break;
            case Holder.ProgramObjectField:
                Slot("SiteLibrary:Destination", "Value", ids);
                Assert.Contains(heap.Regions.Values, region => region.TypeKey == "SiteLibrary:Destination" && region.SiteBodyId?.StartsWith("body:Fixture:", StringComparison.Ordinal) == true);
                break;
            case Holder.ProgramCollection:
                Assert.Contains(heap.Regions.Values, region => region.TypeKey?.Contains("List<", StringComparison.Ordinal) == true &&
                    region.SiteBodyId?.StartsWith("body:Fixture:", StringComparison.Ordinal) == true && heap.PointsTo(region.Identity, "[]").Overlaps(ids));
                break;
            case Holder.ProgramLocal:
            case Holder.ProgramDelegate:
                Assert.All(ids, id => Assert.Contains(id, programValues));
                break;
            case Holder.ProgramStruct:
                Assert.All(ids, id => Assert.Contains(heap.Regions.Values, region => programValues.Contains(region.Identity) &&
                    region.Kind == HeapRegionKind.StructCopy && heap.PointsTo(region.Identity, "Value").Contains(id)));
                if (cell.Accessors == Accessors.LibraryLibrary)
                {
                    Assert.All(ids, id => Assert.DoesNotContain(heap.Regions.Values, region =>
                        programValues.Contains(region.Identity) && region.Kind != HeapRegionKind.StructCopy &&
                        heap.FieldsOf(region.Identity).Any(slot => heap.PointsTo(region.Identity, slot).Contains(id))));
                }
                break;
        }
        var libraryAccesses = accesses.Count(access => access.BodyId.StartsWith("body:SiteLibrary:", StringComparison.Ordinal));
        Assert.Equal(cell.Accessors == Accessors.LibraryLibrary ? accesses.Length : cell.Accessors == Accessors.ProgramProgram ? 0 : accesses.Length / 2, libraryAccesses);

        void Slot(string type, string slot, string[] targets) => Assert.All(targets, target =>
            Assert.Contains(heap.Regions.Values, region => region.TypeKey == type && heap.PointsTo(region.Identity, slot).Contains(target)));
    }

    private static HashSet<string> ProgramValues(HeapSolution heap) => heap.Instances.Values
        .Where(instance => instance.BodyId.StartsWith("body:Fixture:", StringComparison.Ordinal))
        .SelectMany(instance => instance.Receivers.Concat(instance.Parameters.Values.SelectMany(values => values))
            .Concat(instance.Summary.Variables.SelectMany(variable => variable.Values).Concat(instance.Summary.Returns.SelectMany(value => value.Values))
                .Concat(instance.Summary.Calls.Select(call => new CallResultValue(call.OperationId)))
                .SelectMany(value => heap.Resolve(instance.Id, value)))).ToHashSet();

    private static PairAnalysis Cross(Access[] accesses, ScopeRun run)
    {
        var pairs = new List<AccessPair>();
        var skips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var assumed = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var comparisons = 0;
        var suppressed = 0;
        for (var i = 0; i < accesses.Length; i++)
        for (var j = i + 1; j < accesses.Length; j++)
        {
            var first = accesses[i];
            var second = accesses[j];
            if (first.Root.RootId == second.Root.RootId || ReferenceAdmits(first, second, run.Heap!) is null) continue;
            var both = InterproceduralPairing.Pair([first, second], run.Executions!, run.Heap!);
            var one = InterproceduralPairing.Pair([first], run.Executions!, run.Heap!);
            var other = InterproceduralPairing.Pair([second], run.Executions!, run.Heap!);
            comparisons += both.Comparisons - one.Comparisons - other.Comparisons;
            suppressed += both.Suppressed - one.Suppressed - other.Suppressed;
            pairs.AddRange(both.Pairs.Where(pair => pair.First.Root.RootId != pair.Second.Root.RootId));
            Add(skips, both.Skips, one.Skips, other.Skips);
            Add(assumed, both.Assumed, one.Assumed, other.Assumed);
        }
        Assert.True(comparisons > 0);
        return new(pairs, comparisons, suppressed, skips) { Assumed = assumed };

        static void Add(SortedDictionary<string, int> target, IReadOnlyDictionary<string, int> both,
                        IReadOnlyDictionary<string, int> one, IReadOnlyDictionary<string, int> other)
        {
            foreach (var key in both.Keys.Union(one.Keys).Union(other.Keys))
            {
                var count = both.GetValueOrDefault(key) - one.GetValueOrDefault(key) - other.GetValueOrDefault(key);
                if (count != 0) target[key] = target.GetValueOrDefault(key) + count;
            }
        }
    }

    private static void SameExistingCounts(PairAnalysis before, PairAnalysis after)
    {
        Assert.Equal(before.Comparisons, after.Comparisons);
        Assert.Equal(before.Suppressed, after.Suppressed);
        Assert.Equal(before.Assumed, after.Assumed);
        Assert.Equal(before.Skips, after.Skips.Where(pair => pair.Key != InterproceduralPairing.SKIP_LIBRARY_INTERNAL).ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    private static void SamePairing(PairAnalysis indexed, PairAnalysis reference)
    {
        Assert.Equal(reference.Comparisons, indexed.Comparisons);
        Assert.Equal(reference.Suppressed, indexed.Suppressed);
        Assert.Equal(reference.Skips, indexed.Skips);
        Assert.Equal(reference.Assumed, indexed.Assumed);
        Assert.Equal(PairKeys(reference), PairKeys(indexed));
    }

    private static string[] PairKeys(PairAnalysis pairing) => pairing.Pairs.Select(pair =>
        string.Join("|", new[] { pair.First.InstanceId + ":" + pair.First.OperationId + ":" + pair.First.Root.RootId,
                                 pair.Second.InstanceId + ":" + pair.Second.OperationId + ":" + pair.Second.Root.RootId }.Order()) + "|" + pair.Resource.Identity + "|" + pair.Protection)
        .Order(StringComparer.Ordinal).ToArray();

    private static Source Fixture(Cell cell)
    {
        if (cell.Closure != Closure.None)
        {
            var library = "public static class Entry { public static readonly Action SavedDelegate = Make(); " +
                "private static Action Make() { int captured = 0; return () => { captured++; }; } " +
                "public static Action Get() => SavedDelegate; public static void Visit(Action action) { action(); } public static void Visit() { SavedDelegate(); } }";
            return new(library, Workers(cell.Closure == Closure.ClosureKeptByLibrary ? string.Empty : "private readonly Action SavedDelegate = Entry.Get();",
                cell.Closure == Closure.ClosureKeptByLibrary ? "Entry.Visit();" : "Entry.Visit(SavedDelegate);"), AnalysisLimits.Default);
        }
        var type = cell.Storage switch { Storage.CollectionStructure or Storage.CollectionCell => "List<int>", Storage.ArrayCellByReference => "int[]", _ => "Item<int>" };
        var make = cell.Storage switch { Storage.CollectionStructure or Storage.CollectionCell => "new List<int> { 0 }", Storage.ArrayCellByReference => "new int[1]", _ => "new Item<int>()" };
        var declarations = "public struct Part { public int Hits; } public sealed class Item<T> { public int Hits; public Part Nested; } " +
            $"public struct Carrier {{ public {type} Value; }} public sealed class Destination {{ public {type} Value = null!; }} ";
        var limits = AnalysisLimits.Default;
        var maker = string.Empty;
        var initialize = make;
        if (cell.Merge == Merge.ContextLimit)
        {
            limits = limits with { MaxContextsPerMethod = 1 };
            maker = $"public static class Maker {{ public static {type} Make(object marker) => {make}; }}";
            initialize = "Maker.Make(new object())";
        }
        else if (cell.Merge == Merge.OpenGroup)
        {
            limits = limits with { MaxContextsPerMethod = 3 };
            maker = "public static class Maker { public static Item<T> Make<T>(object marker) => new Item<T>(); }";
            initialize = "Maker.Make<int>(new object())";
        }
        var prelude = cell.Merge switch
        {
            Merge.ContextLimit => "Helpers.Kept = Maker.Make(new object()); Helpers.Kept = Maker.Make(new object()); Helpers.Kept = Maker.Make(new object());",
            Merge.OpenGroup => "Open<byte>(new object()); Open<byte>(new object()); Open<byte>(new object()); Open<byte>(new object());",
            _ => string.Empty
        };
        // Open exhausts its calling contexts first; Make sees one closed byte substitution and the open substitution,
        // leaving its int allocation unmerged in the same group as the open allocation.
        var open = cell.Merge == Merge.OpenGroup ? "private static void Open<T>(object marker) { Helpers.Kept = Maker.Make<T>(new object()); }" : string.Empty;
        var outer = $"public sealed class Outer {{ public {type} Value; public Outer({type} value) {{ Value = value; }} }}";
        var fields = string.Empty;
        var setup = string.Empty;
        var load = string.Empty;
        var entryFields = $"private static readonly object Gate = new object(); private static {type} Saved = null!; private static Outer SavedOuter = null!; " +
            (cell.Merge == Merge.OpenGroup
                ? $"static Entry() {{ Saved = {initialize}; {prelude} SavedOuter = new Outer(Saved); }}"
                : $"static Entry() {{ {prelude} Saved = {initialize}; SavedOuter = new Outer(Saved); }}");
        var argument = string.Empty;
        var parameter = string.Empty;
        var resultType = "void";
        var returned = string.Empty;
        var call = "Entry.Visit(";
        if (cell.Creator == Creator.Program)
        {
            entryFields = $"private static {type} Saved = null!; private static readonly Outer SavedOuter = new Outer(null!);";
            fields = $"private readonly {type} _x = {make};";
            parameter = $"{type} input";
            argument = "_x";
            setup = "Saved = input; SavedOuter.Value = input;";
        }
        switch (cell.Holder)
        {
            case Holder.LibraryStatic: load = "var x = Saved;"; break;
            case Holder.LibraryObject: load = "var x = SavedOuter.Value;"; break;
            case Holder.InsideHeld:
                fields = "private readonly Outer _outer = Entry.GetOuter();";
                parameter = "Outer outer"; argument = "_outer"; load = "var x = outer.Value;"; break;
            case Holder.ProgramLocal:
                load = "var x = Saved;"; resultType = type; returned = "return x;"; break;
            case Holder.ProgramField:
                if (cell.Creator == Creator.Library) fields = $"private readonly {type} _x = Entry.Get();";
                parameter = $"{type} input"; argument = "_x"; load = "var x = input;"; break;
            case Holder.ProgramDelegate:
                parameter = $"Action<{type}> action"; argument = "x => { PROGRAM }"; load = "var x = Saved;"; returned = "action(x);"; break;
            case Holder.ProgramObjectField:
                fields = "private readonly Destination _destination = new Destination();";
                parameter = "Destination destination"; argument = "_destination"; setup = "destination.Value = Saved;"; load = "var x = destination.Value;"; break;
            case Holder.ProgramCollection:
                fields = $"private readonly System.Collections.Generic.List<{type}> _items = new();";
                parameter = $"List<{type}> items"; argument = "_items"; setup = "items.Add(Saved);"; load = "var x = items[0];"; break;
            case Holder.LibraryStruct:
                load = "var carrier = new Carrier { Value = Saved }; Copy(carrier); var x = Saved;"; break;
            case Holder.ProgramStruct:
                load = "var x = Saved;"; resultType = "Carrier"; returned = "return new Carrier { Value = x };"; break;
        }
        string Touch(bool read) => cell.Storage switch
        {
            Storage.Field => read ? "_ = x.Hits;" : cell.Operation == Operation.ReadModifyWrite ? "x.Hits++;" : "x.Hits = 1;",
            Storage.StructField => read ? "_ = x.Nested.Hits;" : "x.Nested.Hits = 1;",
            Storage.CollectionStructure => read ? "_ = x.Count;" : "x.Add(1);",
            Storage.CollectionCell => read ? "_ = x[0];" : "x[0] = 1;",
            Storage.ArrayCellByReference => "{ ref int cell = ref x[0]; " + (read ? "_ = cell;" : "cell = 1;") + " }",
            _ => throw new ArgumentOutOfRangeException(nameof(cell))
        };
        var first = Touch(cell.Earlier == Earlier.ReadRead);
        var second = Touch(cell.Earlier == Earlier.ReadRead || cell.Operation == Operation.WriteRead);
        if (cell.Operation == Operation.UnknownEffect) first = second = "Missing.Touch(x);";
        var libraryWork = cell.Accessors switch { Accessors.LibraryLibrary => first + second, Accessors.LibraryProgram => first, _ => string.Empty };
        var programWork = cell.Accessors switch { Accessors.ProgramProgram => first + second, Accessors.LibraryProgram => second, _ => string.Empty };
        if (cell.Earlier == Earlier.Protected) libraryWork = "lock (Gate) { " + libraryWork + " }";
        argument = argument.Replace("PROGRAM", programWork, StringComparison.Ordinal);
        var work = call + argument + ");";
        if (cell.Holder == Holder.ProgramLocal) work = "var x = " + work + programWork;
        else if (cell.Holder == Holder.ProgramStruct)
            work = "var carrier = " + work + (cell.Accessors == Accessors.LibraryLibrary ? string.Empty : "var x = carrier.Value;" + programWork);
        else if (cell.Holder != Holder.ProgramDelegate && cell.Accessors != Accessors.LibraryLibrary)
        {
            var programLoad = cell.Creator == Creator.Program || cell.Holder == Holder.ProgramField ? "var x = _x;" : cell.Holder switch
            {
                Holder.ProgramObjectField => "var x = _destination.Value;",
                Holder.ProgramCollection => "var x = _items[0];",
                _ => throw new ArgumentOutOfRangeException(nameof(cell))
            };
            work += programLoad + programWork;
        }
        // A bodyless, unmodelled call gives an unknown effect on X, without making it a program value.
        var missing = cell.Operation == Operation.UnknownEffect ? $"public static class Missing {{ public static extern void Touch({type} value); }}" : string.Empty;
        var libraryText = declarations + outer + maker + missing + $"public static class Entry {{ {entryFields} {open} " +
            $"public static {type} Get() => Saved; public static Outer GetOuter() => SavedOuter; " +
            $"private static void Copy(Carrier carrier) {{ _ = carrier.Value; }} public static {resultType} Visit({parameter}) {{ {setup} {load} {libraryWork} {returned} }} }}";
        return new(libraryText, Workers(fields, work), limits);
    }

    private static string Workers(string fields, string work) => Usings + "using System.Collections.Generic;" + $$"""
        public sealed class Shared
        {
            {{fields}}
            public async Task Run() { {{work}} }
        }
        public sealed class LeftWorker(Shared shared) : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken token) { await shared.Run(); }
        }
        public sealed class RightWorker(Shared shared) : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken token) { await shared.Run(); }
        }
        """ + Startup("services.AddSingleton<Shared>(); services.AddHostedService<LeftWorker>(); services.AddHostedService<RightWorker>();");
}
