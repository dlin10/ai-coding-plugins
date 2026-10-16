using System.Collections.Concurrent;
using Common.Roslyn;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Core.Tests.Generation;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Struct storage that code the analysis does not see may write may hold a value, as after a copy (R9): a <c>Nullable</c> read
/// from it boxes, and the member called through the box runs with the contents the heap knows. Two hosted workers each write a
/// <c>S?</c> place through one channel, box it and call <c>IS.Bump</c> on the box, which increments a static counter and the
/// <c>Count</c> of the item the struct holds. Which slots unseen code may write is one rule, shared by the heap and the accesses
/// (<see cref="UnseenWrites"/>): the receiver restriction, <c>readonly</c> storage written only by reflection and the depth bound of
/// TD-042.</summary>
public sealed class NullableUnseenWriteMatrixTests
{
    /// <summary>How the place is written before it is read.</summary>
    public enum Channel
    {
        /// <summary>The holder handed to a library call with no body or model.</summary>
        OpaqueCallOnHolder,

        /// <summary><c>Opaque.Lib.TryGet(out place)</c>.</summary>
        OpaqueOut,

        /// <summary><c>Opaque.Lib.Fill(ref place)</c>.</summary>
        OpaqueRef,

        /// <summary>A project model's <c>writes-arg</c> on the holder.</summary>
        ModelWritesArg,

        /// <summary>A project model's <c>writes-cells</c> on the array.</summary>
        ModelWritesCells,

        /// <summary><c>FieldInfo.SetValue</c> on the holder.</summary>
        Reflection,

        /// <summary>The holder handed to a library call, the field holding the storage <c>readonly</c>.</summary>
        ReadonlyUnderOpaqueCall,

        /// <summary><c>FieldInfo.SetValue</c> on the holder, the field holding the storage <c>readonly</c>.</summary>
        ReadonlyUnderReflection,

        /// <summary>A project model's <c>out</c> output written into the place.</summary>
        ModelOut,

        /// <summary>A project model's <c>ref</c> output written into the place.</summary>
        ModelRef,

        /// <summary>The holder derives from a library base type and calls a member of that base on <c>this</c>, which reaches none of
        /// the fields the source declares.</summary>
        BaseMemberOnHolder,

        /// <summary>The same base member called on <c>this</c> and handed <c>this</c> as an argument, which reaches them.</summary>
        ReceiverAndArgument,

        /// <summary>The holder handed to an interface member with a source body called on a field no object is ever stored into: an
        /// unresolved dispatch with no receiver object, which sees what it is handed.</summary>
        UnresolvedDispatch,

        /// <summary>A project model's <c>writes-arg</c> on a sequence parameter, handed a library sequence a model makes of the holder.</summary>
        ModelWritesSequence,

        /// <summary>A project model's <c>writes-arg</c> on the holder, deferred to the enumeration of the sequence the call returns, which a
        /// <c>foreach</c> enumerates.</summary>
        DeferredModelWrite,

        /// <summary>The deferred write's sequence escapes through a static field, an opaque argument or a return.</summary>
        EscapedDeferredModelWrite,

        /// <summary><c>place = new S()</c>, control.</summary>
        VisibleStore,

        /// <summary><c>place = null</c>, control.</summary>
        VisibleNull,

        /// <summary>Nothing writes the place, control.</summary>
        None
    }

    /// <summary>Where the <c>S?</c> place is.</summary>
    public enum Storage
    {
        /// <summary>A field of the singleton holder.</summary>
        InstanceField,

        /// <summary>A static field.</summary>
        StaticField,

        /// <summary>Cell 0 of an array the holder keeps in a field.</summary>
        ArrayCell,

        /// <summary>A field of a struct the holder keeps in a field.</summary>
        NestedField,

        /// <summary>A <c>readonly S?</c> field of a struct the holder keeps in a writable field.</summary>
        NestedReadonlyInner,

        /// <summary>A variable a lambda captures.</summary>
        CaptureCell,

        /// <summary>A field under nested struct fields deeper than the bound of TD-042.</summary>
        PastDepthBound,

        /// <summary>The field of an object one execution creates and hands to the call itself.</summary>
        OwnedObjectField,

        /// <summary>Cell <c>[0, 0]</c> of a rectangular array the holder keeps in a field.</summary>
        RectangularArrayCell,

        /// <summary>The <c>S? Inner</c> of the struct an <c>Outer?</c> field of the holder holds, written through the field and boxed as
        /// <c>_nn.Value.Inner</c>.</summary>
        NestedNullable,

        /// <summary>A nullable field whose held type is the generic holder's struct parameter.</summary>
        GenericHolder,

        /// <summary>A nullable field declared by a metadata base, closed over the source struct.</summary>
        MetadataBaseField,

        /// <summary>A readonly nullable field of a metadata base, writable only by reflection.</summary>
        MetadataReadonlyBaseField
    }

    /// <summary>How the place is boxed and the interface member called on the box.</summary>
    public enum Read
    {
        /// <summary><c>object o = place; ((IS)o).Bump();</c></summary>
        Box,

        /// <summary><c>if (place is IS i) i.Bump();</c></summary>
        PatternBox
    }

    /// <summary>What the place holds before the channel writes it.</summary>
    public enum Prior
    {
        /// <summary>Nothing visible writes the place before the channel.</summary>
        Empty,

        /// <summary>A visible store of a struct holding the shared item writes it before the channel, in the holder's constructor for a
        /// readonly declaration.</summary>
        Filled
    }

    /// <summary>A cell of the matrix.</summary>
    /// <param name="Channel">How the place is written.</param>
    /// <param name="Storage">Where the place is.</param>
    /// <param name="Read">How the place is boxed.</param>
    /// <param name="Prior">What the place holds before the channel.</param>
    private sealed record Cell(Channel Channel, Storage Storage, Read Read, Prior Prior)
    {
        public override string ToString() => $"{Channel}/{Storage}/{Read}/{Prior}";
    }

    /// <summary>The cells whose outcome is known to be wider than the requirement asks: none.</summary>
    private static readonly Cell[] KnownWider = [];

    private static readonly ConcurrentDictionary<Cell, Lazy<EngineRun>> Runs = new();

    private static IEnumerable<Cell> AllCells() =>
        from channel in Enum.GetValues<Channel>()
        from storage in Enum.GetValues<Storage>()
        from read in Enum.GetValues<Read>()
        from prior in Enum.GetValues<Prior>()
        select new Cell(channel, storage, read, prior);

    private static IEnumerable<Cell> Cells() => AllCells().Where(cell => Excluded(cell) is null);

    /// <summary>Why a cell is left out, or null.</summary>
    /// <param name="cell">The cell.</param>
    private static string? Excluded(Cell cell) => (cell.Channel, cell.Storage) switch
    {
        (_, Storage.MetadataReadonlyBaseField) when cell.Prior != Prior.Empty ||
            cell.Channel is not (Channel.OpaqueCallOnHolder or Channel.BaseMemberOnHolder or Channel.Reflection or Channel.None) =>
            "readonly metadata storage is written only by reflection; C# rejects filling it from the source constructor",
        (_, Storage.MetadataBaseField) when cell.Channel is not (Channel.OpaqueCallOnHolder or Channel.BaseMemberOnHolder or Channel.ModelWritesArg or Channel.None) =>
            "metadata-base rows exercise receiver and argument effects and unchanged empty storage",
        (_, Storage.GenericHolder) when cell.Channel is not (Channel.OpaqueCallOnHolder or Channel.ReadonlyUnderOpaqueCall or Channel.None or Channel.VisibleNull) =>
            "generic-holder rows exercise an unseen write and unchanged empty or readonly controls",
        (Channel.ModelWritesCells, not (Storage.ArrayCell or Storage.RectangularArrayCell)) => "the model writes the cells of an array argument",
        (Channel.ModelWritesArg or Channel.OpaqueCallOnHolder or Channel.BaseMemberOnHolder or Channel.ReceiverAndArgument or Channel.Reflection or
         Channel.ReadonlyUnderOpaqueCall or Channel.ReadonlyUnderReflection or Channel.UnresolvedDispatch or Channel.ModelWritesSequence or
         Channel.DeferredModelWrite or Channel.EscapedDeferredModelWrite, Storage.StaticField or Storage.CaptureCell) =>
            "an argument or a receiver reaches neither",
        (Channel.BaseMemberOnHolder or Channel.ReceiverAndArgument, Storage.OwnedObjectField) => "the object is no holder deriving from the base",
        (Channel.ReadonlyUnderOpaqueCall or Channel.ReadonlyUnderReflection, Storage.NestedReadonlyInner) => "its storage is readonly in every channel",
        (Channel.OpaqueOut or Channel.OpaqueRef or Channel.ModelOut or Channel.ModelRef or Channel.VisibleStore or Channel.VisibleNull,
         Storage.NestedReadonlyInner) => "CS0191: C# rejects writing a readonly field outside its constructor",
        _ => null
    };

    public static TheoryData<Channel, Storage, Read, Prior> Rows()
    {
        var rows = new TheoryData<Channel, Storage, Read, Prior>();
        foreach (var cell in Cells())
            rows.Add(cell.Channel, cell.Storage, cell.Read, cell.Prior);
        return rows;
    }

    /// <summary>Whether the cell boxes (R9): under Prior Filled every cell, the visible store having filled the place; under Prior Empty
    /// where unseen code may write the place, or a visible store of a struct does.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Expected(Cell cell) =>
        cell.Storage == Storage.MetadataReadonlyBaseField ? cell.Channel == Channel.Reflection :
        cell.Prior == Prior.Filled || cell.Storage == Storage.MetadataBaseField && cell.Channel == Channel.BaseMemberOnHolder || cell.Channel switch
        {
            Channel.OpaqueCallOnHolder or Channel.OpaqueOut or Channel.OpaqueRef or Channel.ModelOut or Channel.ModelRef or Channel.ModelWritesCells or
                Channel.Reflection or Channel.ReadonlyUnderReflection or Channel.ReceiverAndArgument or Channel.UnresolvedDispatch or Channel.VisibleStore => true,
            // The model writes the holder's own fields, a struct field whole; the array's cells are no field of the holder. Handed the
            // holder in a library sequence or deferred to an enumeration, it writes the same.
            Channel.ModelWritesArg or Channel.ModelWritesSequence or Channel.DeferredModelWrite or Channel.EscapedDeferredModelWrite => !IsArray(cell.Storage),
            // A readonly array field leaves its cells writable; an unknown effect only reads readonly storage and all of it below.
            Channel.ReadonlyUnderOpaqueCall => IsArray(cell.Storage),
            _ => false
        };

    /// <summary>Whether the place is a cell of an array, of any rank.</summary>
    /// <param name="storage">Where the place is.</param>
    private static bool IsArray(Storage storage) => storage is Storage.ArrayCell or Storage.RectangularArrayCell;

    [Theory]
    [MemberData(nameof(Rows))]
    public void Every_cell_boxes_as_expected(Channel channel, Storage storage, Read read, Prior prior)
    {
        var cell = new Cell(channel, storage, read, prior);
        var run = Run(cell);
        var expected = Expected(cell);

        Assert.True(expected == (Boxes(run).Length != 0), $"{cell}: expected {(expected ? "a box" : "no box")}");
        Assert.True(expected == run.Accesses("N").Any(access => access.Operation == AccessOperation.ReadModifyWrite), $"{cell}: Bump's counter access");
        Assert.True(expected == run.PairsOn("N").Any(), $"{cell}: the counter's pair across the workers");
    }

    /// <summary>Repeated opaque calls sharing their starts have the same grouped walk cost and storage as one call. Storage no
    /// unseen write reaches stays empty; both boxing forms run Bump again after deferred presence invalidates its readers.</summary>
    /// <param name="read">The boxing form.</param>
    [Theory]
    [InlineData(Read.Box)]
    [InlineData(Read.PatternBox)]
    public void Shared_unseen_starts_are_walked_once_after_propagation(Read read)
    {
        var cell = new Cell(Channel.OpaqueCallOnHolder, Storage.InstanceField, read, Prior.Empty);
        var one = Run(cell);
        var source = Source(cell).Replace("Opaque.Lib.Touch(this);", string.Join(" ", Enumerable.Repeat("Opaque.Lib.Touch(this);", 64)));
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value, ModelLibrary.Value] }, ("Case.cs", source));
        var many = AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, Models.Value));
        var untouched = Run(cell with { Channel = Channel.None });
        Assert.NotEmpty(Boxes(one));
        Assert.NotEmpty(Boxes(many));
        Assert.NotEmpty(many.PairsOn("N"));
        Assert.Empty(Boxes(untouched));
        Assert.Empty(untouched.Execution.Heap.Heap.UnseenFilledStorage);
        Assert.Equal(one.Execution.Heap.Heap.UnseenFilledStorage.Order(), many.Execution.Heap.Heap.UnseenFilledStorage.Order());
        Assert.Equal(one.Execution.Heap.Heap.Counters[HeapCounters.UNSEEN_WALK_VISITS], many.Execution.Heap.Heap.Counters[HeapCounters.UNSEEN_WALK_VISITS]);
        AssertActualWrites(one);
        AssertActualWrites(many);
        AssertActualWrites(untouched);
    }

    /// <summary>The generic holder's visible read and unseen write name the same closed struct storage.</summary>
    /// <param name="read">The boxing form.</param>
    [Theory]
    [InlineData(Read.Box)]
    [InlineData(Read.PatternBox)]
    public void Generic_nullable_storage_reads_pair_with_its_unseen_write(Read read)
    {
        var run = Run(new Cell(Channel.OpaqueCallOnHolder, Storage.GenericHolder, read, Prior.Empty));
        Assert.Contains(run.Accesses("_n"), access => access.Operation == AccessOperation.Read);
        Assert.Contains(run.PairsOn("_n"), pair => pair.First.Operation == AccessOperation.Read || pair.Second.Operation == AccessOperation.Read);
    }

    /// <summary>Handing the deferred sequence to an opaque call also lets that call reach its elements. Both presence and accesses
    /// include that unknown effect as well as the deferred write, for fields and array cells alike.</summary>
    /// <param name="storage">The place reached through the sequence's holder.</param>
    [Theory]
    [InlineData(Storage.InstanceField)]
    [InlineData(Storage.ArrayCell)]
    [InlineData(Storage.NestedNullable)]
    public void An_opaque_argument_escapes_the_deferred_sequence(Storage storage)
    {
        var source = Source(new Cell(Channel.EscapedDeferredModelWrite, storage, Read.Box, Prior.Empty))
            .Replace("Saved = Models.Lib.Defer(this);", "Opaque.Lib.Touch(Models.Lib.Defer(this));");
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value, ModelLibrary.Value] }, ("Case.cs", source));
        var run = AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, Models.Value));
        Assert.NotEmpty(Boxes(run));
        Assert.NotEmpty(run.PairsOn("N"));
        Assert.Equal(run.Execution.Heap.Heap.UnseenFilledStorage.Order(StringComparer.Ordinal),
                     run.Collection.UnseenFilledStorage.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void No_cell_loses_the_box_members_access()
    {
        foreach (var cell in Cells().Where(Expected))
        {
            var run = Run(cell);
            var bumps = run.Accesses("N").Where(access => access.Symbol.Contains("S.Bump", StringComparison.Ordinal)).ToArray();
            Assert.True(bumps.Any(access => access.Operation == AccessOperation.ReadModifyWrite), $"{cell}: Bump's read-modify-write of the counter");
            Assert.True(run.PairsOn("N").Any(pair => pair.First.Symbol != pair.Second.Symbol || pair.First.ExecutionId != pair.Second.ExecutionId),
                        $"{cell}: the counter's pair across the two workers");
        }
    }

    [Fact]
    public void A_box_holds_the_objects_the_heap_knows()
    {
        foreach (var cell in Cells().Where(Expected))
        {
            var run = Run(cell);
            var heap = run.Execution.Heap.Heap;
            var held = Boxes(run).SelectMany(box => heap.PointsTo(box.Identity, "Fixture:S.Ref")).Select(region => heap.Regions[region].TypeKey).ToArray();
            // The unknown effect of a channel reaches the item the holder keeps on its own; only Bump's accesses go through the box.
            var bumps = run.Accesses("Count").Where(access => access.Symbol.Contains("S.Bump", StringComparison.Ordinal)).ToArray();
            if (cell.Prior == Prior.Filled)
            {
                Assert.True(held.Contains("Fixture:Item"), $"{cell}: the box holds the shared item");
                Assert.True(bumps.Any(access => access.Operation == AccessOperation.ReadModifyWrite), $"{cell}: Bump's item access");
                Assert.True(run.PairsOn("Count").Any(pair => pair.First.Symbol.Contains("S.Bump", StringComparison.Ordinal) &&
                                                             pair.Second.Symbol.Contains("S.Bump", StringComparison.Ordinal)),
                            $"{cell}: the item's pair across the workers");
            }
            else
            {
                Assert.True(held.Length == 0, $"{cell}: the box holds an object the heap does not know");
                Assert.True(bumps.Length == 0, $"{cell}: an access of an item the heap does not know");
            }
        }
    }

    /// <summary>The twin of the rule: the struct storage the heap marks as possibly holding a value by an unseen write is the struct
    /// storage the unknown effects and model writes of the same scope write, before an unknown effect on an object one execution owns is
    /// made a read — a wildcard past the depth bound covering each slot holding a <c>Nullable</c> of its region the rule lets it write.
    /// The heap walks on its own and the accesses walk on theirs, so the two agree only where both apply the one rule.</summary>
    [Fact]
    public async Task Marked_storage_is_what_unseen_writes_write()
    {
        foreach (var cell in Cells())
        {
            var run = Run(cell);
            AssertActualWrites(run, cell.ToString());
            Assert.True(run.Execution.Heap.Heap.UnseenFilledStorage.Order(StringComparer.Ordinal)
                           .SequenceEqual(run.Collection.UnseenFilledStorage.Order(StringComparer.Ordinal)),
                        $"{cell}: the heap marked [{string.Join(", ", run.Execution.Heap.Heap.UnseenFilledStorage.Order(StringComparer.Ordinal))}], " +
                        $"the unseen writes wrote [{string.Join(", ", run.Collection.UnseenFilledStorage.Order(StringComparer.Ordinal))}]");
        }

        foreach (var writable in new[] { false, true })
            foreach (var reflection in new[] { false, true })
                AssertActualWrites(DepthRun(writable, reflection));

        // The walks mark something wherever an unknown effect or a model write reaches a Nullable it may write.
        Assert.All(Cells().Where(cell => cell is { Prior: Prior.Empty, Channel: Channel.OpaqueCallOnHolder or Channel.Reflection or Channel.ModelWritesArg or
                                                                                  Channel.ModelWritesCells or Channel.ReceiverAndArgument or
                                                                                  Channel.UnresolvedDispatch or Channel.ModelWritesSequence or
                                                                                  Channel.DeferredModelWrite or Channel.EscapedDeferredModelWrite or Channel.ModelOut or Channel.ModelRef or
                                                                                  Channel.OpaqueOut or Channel.OpaqueRef } && Expected(cell)),
                   cell => Assert.True(Run(cell).Execution.Heap.Heap.UnseenFilledStorage.Count != 0, $"{cell}: nothing marked"));

        await DemoWorkspace.EnsureRestoredAsync();
        var path = RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx");
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(path);
        Assert.True(loaded.Coverage.LoadComplete, string.Join(", ", loaded.Coverage.MissingProjects));
        var rootDirectory = Path.GetDirectoryName(path)!;
        var repository = RepositoryRoot.Find(rootDirectory);
        var projectModels = ProjectModelFiles.Read(repository);
        var modelLock = ModelLock.Read(repository, projectModels);
        var cache = new Dictionary<(Compilation Compilation, string BodyId, LibraryModels Models), IrLoweredMethod?>();
        var scopes = ProcessScopes.Discover(loaded.Solution, rootDirectory).Scopes;
        Assert.NotEmpty(scopes);
        foreach (var scoped in scopes)
        {
            var compiled = new List<(Compilation Compilation, string? ProjectFilePath)>();
            foreach (var project in scoped.Projects)
                compiled.Add(((await project.GetCompilationAsync())!, project.FilePath));
            var compilations = compiled.Select(item => item.Compilation).ToArray();
            var (models, _) = ProjectModelResolver.Resolve(projectModels, compilations, modelLock);
            var scope = ScopePipeline.Run(scoped.Scope.Id, compilations, compiled, rootDirectory, ProviderRegistry.BuiltIn, models,
                                                   AnalysisLimits.Default, cache, null, CancellationToken.None);
            Assert.False(scope.Stopped);
            AssertActualWrites(scope.Heap, scope.Program, scope.Collection, scoped.Scope.Id);
            Assert.Equal(scope.Heap.UnseenFilledStorage.Order(StringComparer.Ordinal), scope.Collection.UnseenFilledStorage.Order(StringComparer.Ordinal));
        }
    }

    /// <summary>Source Nullable storage is filled by an unseen write as metadata Nullable storage is, and untouched storage stays empty.</summary>
    /// <param name="write">Whether the opaque call may write the holder.</param>
    /// <param name="pattern">Whether the read boxes through a pattern rather than a conversion.</param>
    [RequiresModelEvalsTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Source_nullable_unseen_writes_fill_the_slot_and_both_walks_agree(bool write, bool pattern)
    {
        var library = CoreLibGenerationEvalTests.Library.Value.Compilation!;
        var read = pattern ? "if (h.N is IBump b) b.Bump();" : "object boxed = h.N; if (boxed is IBump b) b.Bump();";
        var source = $$"""
            public static class ModelDriver
            {
                public static void V_Setup() { }
                public static void V_Call() { var h = new Holder(); {{(write ? "Touch(h);" : "")}} {{read}} }
                public static extern void Touch(object holder);
            }
            public sealed class Holder { public Probe_T? N; }
            public interface IBump { void Bump(); }
            public struct Probe_T : IBump { public void Bump() { Counter.Hits++; } }
            public static class Counter { public static int Hits; }
            """;
        var driver = CSharpCompilation.Create(DriverSynthesizer.ASSEMBLY,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), "ModelDriver/Unseen.cs")],
            library.References.Append(library.ToMetadataReference()), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(driver.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var models = new LibraryModels(LibraryModels.BuiltIn.Members.Where(model => model.Assemblies.All(range => range.AssemblyName != "System.Private.CoreLib")),
                                       LibraryModels.BuiltIn.ImmutableTypes.Where(type => type.Assemblies.All(range => range.AssemblyName != "System.Private.CoreLib")));
        var rules = new ReachabilityRules(DispatchRule.ConstructedTypes, new HashSet<string> { "System.Private.CoreLib:System.SR" })
        {
            OpenWorldAssemblies = new HashSet<string> { DriverSynthesizer.ASSEMBLY }
        };
        var run = ScopePipeline.Run("source-nullable-unseen", [library, driver], [], Path.GetTempPath(), new ProviderRegistry([new DriverRootProvider()]), models,
                                    AnalysisLimits.Default, new(), 1500, CancellationToken.None, reachability: rules);
        Assert.False(run.Stopped);
        Assert.Equal(run.Heap.UnseenFilledStorage.Order(StringComparer.Ordinal), run.Collection.UnseenFilledStorage.Order(StringComparer.Ordinal));
        Assert.Equal(write, run.Heap.UnseenFilledStorage.Count != 0);
        Assert.Equal(write, run.Heap.Regions.Values.Any(region => region.Kind == HeapRegionKind.Allocation && region.TypeKey == DriverSynthesizer.ASSEMBLY + ":Probe_T"));
        Assert.Equal(write, run.Heap.Instances.Values.Any(instance => instance.BodyId.Contains("Probe_T.Bump", StringComparison.Ordinal)));
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var all = AllCells().ToArray();
        var cells = Cells().ToArray();
        Assert.Empty(KnownWider);
        Assert.Equal(988, all.Length);
        Assert.Equal(348, all.Count(cell => Excluded(cell) is not null));
        Assert.Equal(640, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.All(Enum.GetValues<Channel>(), value => Assert.Contains(cells, cell => cell.Channel == value));
        Assert.All(Enum.GetValues<Storage>(), value => Assert.Contains(cells, cell => cell.Storage == value));
        Assert.All(Enum.GetValues<Read>(), value => Assert.Contains(cells, cell => cell.Read == value));
        Assert.All(Enum.GetValues<Prior>(), value => Assert.Contains(cells, cell => cell.Prior == value));
        // Both outcomes occur under Prior Empty for every storage.
        Assert.All(Enum.GetValues<Storage>(), value => Assert.Contains(cells, cell => cell.Storage == value && cell.Prior == Prior.Empty && Expected(cell)));
        Assert.All(Enum.GetValues<Storage>(), value => Assert.Contains(cells, cell => cell.Storage == value && cell.Prior == Prior.Empty && !Expected(cell)));
    }

    /// <summary>A readonly Nullable past the object depth bound remains a read; a writable neighbor still conflicts.</summary>
    /// <param name="writable">Whether the leaf has a writable Nullable beside its readonly field.</param>
    /// <param name="reflection">Whether the unseen channel may also assign readonly storage.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Past_the_object_bound_preserves_readonly_storage(bool writable, bool reflection)
    {
        var run = DepthRun(writable, reflection);
        AssertActualWrites(run);
        Assert.Contains(run.Collection.Accesses, access => access.Resource.Storage?.Fields.LastOrDefault()?.Name == "Last" && access.Operation == AccessOperation.Read);
        Assert.Equal(reflection, run.Pairs.Pairs.Any(pair => new[] { pair.First, pair.Second }.Any(access =>
            access.Resource.Storage?.Fields.LastOrDefault()?.Name == "Last" && access.Operation == AccessOperation.Read)));
        if (writable)
            Assert.Contains(run.Pairs.Pairs, pair => new[] { pair.First, pair.Second }.Any(access =>
                access.Resource.Storage?.Fields.LastOrDefault()?.Name == "Mutable" && access.Operation == AccessOperation.Read));
        AssertActualWrites(run);
    }

    /// <summary>The actual unseen writes cover exactly the presence slots marked by the heap, before ownership downgrades.</summary>
    /// <param name="run">The full engine run.</param>
    /// <param name="cell">The matrix row identifying a failure.</param>
    private static void AssertActualWrites(EngineRun run, string cell = "depth") =>
        AssertActualWrites(run.Execution.Heap.Heap, run.Execution.Heap.Program.Input.Program, run.Collection, cell);

    /// <summary>Expands emitted writes over the heap's struct storage, independently of the collector's presence bookkeeping.</summary>
    /// <param name="heap">The solved heap.</param>
    /// <param name="program">The scope's type index.</param>
    /// <param name="collection">The actual emitted accesses.</param>
    /// <param name="cell">The scope or matrix row identifying a failure.</param>
    private static void AssertActualWrites(HeapSolution heap, ProgramIndex program, InterproceduralCollection collection, string cell)
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var access in collection.Accesses.Where(access => access.UnseenWriteOperation is not null))
        {
            var resource = access.Resource;
            if (resource.IsWildcard)
            {
                // Expand the resource actually emitted, without using the collector's presence bookkeeping.
                var region = heap.Regions[resource.RegionId!];
                foreach (var field in (program.InstanceFieldsOf(region.TypeKey!) ?? []).Where(field => !resource.WildcardExclusions.Contains(FieldSlot.Key(field))))
                    foreach (var slot in UnseenWrites.PresenceSlots(program, field.FieldTypeKey, FieldSlot.Key(field)))
                        written.Add(heap.StorageIdentity(region.Identity, slot));
            }
            else if (resource.Storage is { } storage)
            {
                if (resource.Selector is not null && resource.CollectionId is { } array && storage.Cell == storage.Fields.Count)
                {
                    foreach (var slot in UnseenWrites.PresenceSlots(program, UnseenWrites.ElementTypeKey(heap.Regions[array].TypeKey), PathValue.ELEMENT))
                        written.Add(heap.StorageIdentity(array, slot));
                    continue;
                }
                var type = resource.Selector is not null && storage.Cell == storage.Fields.Count
                    ? UnseenWrites.ElementTypeKey(heap.Regions[storage.Region].TypeKey)
                    : storage.Fields.LastOrDefault()?.FieldTypeKey ?? heap.Regions[storage.Region].TypeKey;
                type = type is null ? null : ProgramIndex.Substitute(type, heap.Instances[access.InstanceId].Substitution);
                var slots = UnseenWrites.PresenceSlots(program, type, storage.Slot);
                foreach (var slot in slots)
                    written.Add(heap.StorageIdentity(storage.Region, slot));
                // An unseen output writes the struct target whole, Nullable or not.
                var instance = heap.Instances[access.InstanceId];
                if (access.WritesWholeStruct || instance.Summary.StructCopies.Any(copy => copy.IsUnseenOutput && copy.OperationId == access.OperationId))
                    written.Add(heap.StorageIdentity(storage.Region, storage.Slot));
            }
        }
        Assert.True(heap.UnseenFilledStorage.SetEquals(written),
                    $"{cell}: heap marked [{string.Join(", ", heap.UnseenFilledStorage.Order())}], actual writes cover [{string.Join(", ", written.Order())}]");
    }

    /// <summary>An opaque effect traverses an object chain to a leaf with readonly storage and optionally writable storage.</summary>
    /// <param name="writable">Whether the leaf also has a writable field.</param>
    /// <param name="reflection">Whether the effect is reflection.</param>
    private static EngineRun DepthRun(bool writable, bool reflection)
    {
        var chain = "leaf";
        for (var i = 8; i >= 0; i--)
            chain = $"new N{i}({chain})";
        var nodes = string.Join("\n", Enumerable.Range(0, 9).Select(i =>
            $"public sealed class N{i}({(i == 8 ? "Leaf" : "N" + (i + 1))} next) {{ public readonly {(i == 8 ? "Leaf" : "N" + (i + 1))} Next = next; }}"));
        var source = Usings + $$"""
            public struct S { public int X; }
            public sealed class Leaf
            {
                public readonly S? Last;
                {{(writable ? "public S? Mutable; public S? ReadMutable() => Mutable;" : "")}}
                public S? Read() => Last;
            }
            {{nodes}}
            public sealed class Holder
            {
                private readonly N0 _chain;
                public Holder(Leaf leaf) { _chain = {{chain}}; }
                public void Touch() { {{(reflection ? "typeof(Holder).GetField(\"_chain\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(this, null);" : "Opaque.Lib.Touch(this);")}} }
            }
            public sealed class FirstWorker(Holder holder) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { holder.Touch(); return Task.CompletedTask; }
            }
            public sealed class SecondWorker(Leaf leaf) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { leaf.Read(); {{(writable ? "leaf.ReadMutable();" : "")}} return Task.CompletedTask; }
            }
            """ + Startup("services.AddSingleton<Leaf>(); services.AddSingleton<Holder>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();");
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] }, ("Case.cs", source));
        return AnalyzeScope(solution, "scope:Fixture");
    }

    /// <summary>The boxes of <c>S</c> the heap made.</summary>
    /// <param name="run">The cell's run.</param>
    private static HeapRegion[] Boxes(EngineRun run) =>
        run.Execution.Heap.Heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Allocation && region.TypeKey == "Fixture:S").ToArray();

    private static EngineRun Run(Cell cell) =>
        Runs.GetOrAdd(cell, _ => new Lazy<EngineRun>(() =>
        {
            var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value, ModelLibrary.Value] }, ("Case.cs", Source(cell)));
            return AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, Models.Value));
        })).Value;

    /// <summary>The case of a cell: a singleton holder whose <c>Run</c> both hosted workers call.</summary>
    /// <param name="cell">The cell.</param>
    private static string Source(Cell cell)
    {
        var isReadonly = cell.Channel is Channel.ReadonlyUnderOpaqueCall or Channel.ReadonlyUnderReflection;
        var modifier = isReadonly ? "readonly " : "";
        var (field, place, root) = cell.Storage switch
        {
            Storage.MetadataBaseField or Storage.MetadataReadonlyBaseField => ("", "N", "N"),
            Storage.GenericHolder => ($"private {modifier}T? _n;", "_n", "_n"),
            Storage.InstanceField => ($"private {modifier}S? _n;", "_n", "_n"),
            Storage.StaticField => ("private static S? s_n;", "s_n", "s_n"),
            Storage.ArrayCell => ($"private {modifier}S?[] _arr = new S?[4];", "_arr[0]", "_arr"),
            Storage.NestedField => ($"private {modifier}Outer _outer;", "_outer.Inner", "_outer"),
            Storage.NestedReadonlyInner => ("private ReadonlyOuter _outer;", "_outer.Inner", "_outer"),
            Storage.CaptureCell => ("", "n", ""),
            Storage.PastDepthBound => ($"private {modifier}D0 _d;", "_d.A.A.A.A.A.A.A.A.Inner", "_d"),
            Storage.OwnedObjectField => ("", "owned._n", "_n"),
            Storage.RectangularArrayCell => ($"private {modifier}S?[,] _arr = new S?[2, 2];", "_arr[0, 0]", "_arr"),
            Storage.NestedNullable => ($"private {modifier}Outer? _nn;", "_nn", "_nn"),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell.Storage, null)
        };
        // A Nullable of a struct holding a Nullable is written whole and boxed by what its value holds.
        var nested = cell.Storage == Storage.NestedNullable;
        var (boxed, placeType) = nested ? ("_nn.Value.Inner", "Outer?") : (place, "S?");
        string Filling(string held) => cell.Storage == Storage.GenericHolder ? $"{place} = (T)(object)({held});" : nested ? $"_nn = new Outer {{ Inner = {held} }};" : $"{place} = {held};";
        var holder = cell.Storage == Storage.OwnedObjectField ? "owned" : "this";
        var holderType = cell.Storage == Storage.OwnedObjectField ? "Owned" : "Holder";
        var channel = cell.Channel switch
        {
            Channel.OpaqueCallOnHolder or Channel.ReadonlyUnderOpaqueCall => $"Opaque.Lib.Touch({holder});",
            Channel.OpaqueOut => $"Opaque.Lib.TryGet(out {place});",
            Channel.OpaqueRef => $"Opaque.Lib.Fill(ref {place});",
            Channel.ModelWritesArg => $"Models.Lib.Write({holder});",
            Channel.ModelWritesCells => $"Models.Lib.{(cell.Storage == Storage.RectangularArrayCell ? "Cells2" : "Cells")}({place[..place.IndexOf('[')]});",
            Channel.Reflection or Channel.ReadonlyUnderReflection => $"typeof({holderType}).GetField(\"{root}\").SetValue({holder}, null);",
            Channel.ModelOut => $"Models.Lib.Pass<{placeType}>(null, out {place});",
            Channel.ModelRef => $"Models.Lib.Replace<{placeType}>(null, ref {place});",
            Channel.BaseMemberOnHolder => "Touch();",
            Channel.ReceiverAndArgument => "Take(this);",
            Channel.UnresolvedDispatch => $"_filler.Fill({holder});",
            Channel.ModelWritesSequence => $"Models.Lib.WriteAll(Models.Lib.Wrap({holder}));",
            Channel.DeferredModelWrite => $"foreach (var item in Models.Lib.Defer({holder})) {{ }}",
            Channel.EscapedDeferredModelWrite => cell.Read == Read.Box
                ? $"Saved = Models.Lib.Defer({holder});"
                : $"Saved = Escape(Models.Lib.Defer({holder}));",
            Channel.VisibleStore => Filling("new S()"),
            Channel.VisibleNull => nested ? "_nn = null;" : $"{place} = null;",
            Channel.None => "",
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell.Channel, null)
        };
        var read = cell.Read == Read.Box ? $"object o = {boxed}; ((IS)o).Bump();" : $"if ({boxed} is IS i) i.Bump();";
        // A readonly declaration is filled in the constructor of the object declaring it; any other place before the channel.
        var filledInConstructor = cell.Prior == Prior.Filled && (isReadonly || cell.Storage == Storage.NestedReadonlyInner);
        var fill = cell.Prior == Prior.Filled && !filledInConstructor
            ? cell.Storage == Storage.NestedReadonlyInner ? "" : Filling("new S { Ref = _item }")
            : "";
        var holderConstructor = filledInConstructor && cell.Storage != Storage.OwnedObjectField
            ? cell.Storage == Storage.NestedReadonlyInner ? "_outer = new ReadonlyOuter(new S { Ref = _item });" : Filling("new S { Ref = _item }")
            : "";
        var ownedConstructor = filledInConstructor && cell.Storage == Storage.OwnedObjectField ? "_n = new S { Ref = item };" : "";
        var body = $"{fill} {channel} {read}";
        // The lambda writes the variable and the declaring body reads it after running the lambda, through the cell.
        if (cell.Storage == Storage.CaptureCell)
            body = $"S? n = null; Action write = () => {{ {fill} {channel} }}; write(); {read}";
        else if (cell.Storage == Storage.OwnedObjectField)
            body = $"var owned = new Owned(_item); {body}";
        var baseType = cell.Storage == Storage.MetadataReadonlyBaseField ? " : Opaque.ReadonlyBase<S>"
            : cell.Storage == Storage.MetadataBaseField ? " : Opaque.Base<S>"
            : cell.Channel is Channel.BaseMemberOnHolder or Channel.ReceiverAndArgument ? " : Opaque.Base" : "";
        var holderDeclaration = cell.Storage == Storage.GenericHolder ? "Holder<T> where T : struct, IS" : "Holder" + baseType;
        var workerHolder = cell.Storage == Storage.GenericHolder ? "Holder<S>" : "Holder";
        return Usings + $$"""
            public sealed class Item { public int Count; }
            public interface IS { void Bump(); }
            public interface IFiller { void Fill(object value) { } }
            public static class Counter { public static int N; }
            public struct S : IS { public Item Ref; public void Bump() { Counter.N++; Ref.Count++; } }
            public struct Outer { public S? Inner; }
            public struct ReadonlyOuter { public readonly S? Inner; public ReadonlyOuter(S? inner) { Inner = inner; } }
            public struct D0 { public D1 A; }
            public struct D1 { public D2 A; }
            public struct D2 { public D3 A; }
            public struct D3 { public D4 A; }
            public struct D4 { public D5 A; }
            public struct D5 { public D6 A; }
            public struct D6 { public D7 A; }
            public struct D7 { public D8 A; }
            public struct D8 { public S? Inner; }
            public sealed class Owned
            {
                public {{(isReadonly ? "readonly " : "")}}S? _n;
                public Owned(Item item) { {{ownedConstructor}} }
            }
            public sealed class {{holderDeclaration}}
            {
                public static object Saved;
                public static object Escape(object sequence) => sequence;
                private readonly Item _item = new Item();
                private IFiller _filler;
                {{field}}
                public Holder() { {{holderConstructor}} }
                public void Run() { {{body}} }
            }
            public sealed class FirstWorker({{workerHolder}} holder) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { holder.Run(); return Task.CompletedTask; }
            }
            public sealed class SecondWorker({{workerHolder}} holder) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { holder.Run(); return Task.CompletedTask; }
            }
            """ + Startup($"services.AddSingleton<{workerHolder}>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();");
    }

    /// <summary>A library the run has no source and no model of.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() => Emit("Opaque", """
        namespace Opaque;
        public static class Lib
        {
            public static void Touch(object value) { }
            public static bool TryGet<T>(out T value) { value = default!; return false; }
            public static void Fill<T>(ref T value) { }
        }
        public class ReadonlyBase<T> where T : struct
        {
            public readonly T? N;
            public void Touch() { }
        }
        public class Base<T> where T : struct
        {
            public T? N;
            public void Touch() { }
        }
        public class Base
        {
            public void Touch() { }
            public void Take(object value) { }
        }
        """));

    private static readonly Lazy<CSharpCompilation> ModelSource = new(() => Compile("Models", """
        using System.Collections.Generic;
        namespace Models;
        public static class Lib
        {
            public static void Write(object value) { }
            public static void Cells<T>(T[] array) { }
            public static void Cells2<T>(T[,] array) { }
            public static void Pass<T>(T source, out T value) { value = source; }
            public static void Replace<T>(T source, ref T value) { }
            public static void WriteAll<T>(IEnumerable<T> values) { }
            public static IEnumerable<T> Wrap<T>(T value) => null;
            public static IEnumerable<T> Defer<T>(T value) => null;
        }
        """));

    /// <summary>A library a project model describes.</summary>
    private static readonly Lazy<MetadataReference> ModelLibrary = new(() => Emit(ModelSource.Value));

    private static readonly Lazy<string> Models = new(() =>
    {
        var lib = ModelSource.Value.GetTypeByMetadataName("Models.Lib")!;
        string Entry(string name, string properties) =>
            "{\"member\":\"" + DocumentationCommentId.CreateDeclarationId(lib.GetMembers(name).OfType<IMethodSymbol>().Single()) + "\"," + properties + "}";
        return """{"schemaVersion":1,"assemblies":["Models"],"models":[""" + string.Join(",",
            Entry("Write", "\"effects\":{\"value\":[\"writes-arg\"]}"),
            Entry("Cells", "\"effects\":{\"array\":[\"writes-cells\"]}"),
            Entry("Cells2", "\"effects\":{\"array\":[\"writes-cells\"]}"),
            Entry("Pass", "\"effects\":{},\"outputs\":{\"value\":\"[arg:source]\"}"),
            Entry("Replace", "\"effects\":{},\"outputs\":{\"value\":\"[arg:source]\"}"),
            Entry("WriteAll", "\"effects\":{\"values\":[\"writes-arg\"]}"),
            Entry("Wrap", "\"effects\":{},\"result\":\"sequence(arg:value)\""),
            // A model whose result is a sequence defers its effects to the sequence's enumeration.
            Entry("Defer", "\"effects\":{\"value\":[\"writes-arg\"]},\"result\":\"sequence(arg:value)\"")) + "]}";
    });

    private static CSharpCompilation Compile(string name, string source) =>
        CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
                                 StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static MetadataReference Emit(string name, string source) => Emit(Compile(name, source));

    private static MetadataReference Emit(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }
}
