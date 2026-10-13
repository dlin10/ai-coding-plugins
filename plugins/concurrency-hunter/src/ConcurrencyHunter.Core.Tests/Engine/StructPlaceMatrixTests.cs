using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>
/// Every place a struct is kept in, reached by every route, used every way, end to end (R1, R2). Heap storage — an instance field, a
/// static field, an array cell, a nested struct field, a capture cell — is the holder region's storage, and a use of it is the matching
/// access on (holder region, path to the field), a member's body included through <c>this</c>; a readonly declaration or a readonly
/// reference makes a member that is not <c>readonly</c> run on a defensive copy, a read of the whole struct; a box is a region of its own
/// reached through its members; a local, a by-value parameter and a call's result are copies, no location at all. Through every copy the
/// reference field still points at the singleton's one object, and a method-group delegate boxes a copy that only its maker reaches.
/// Two hosted workers of one singleton perform the use, or, for a capture cell, two <c>Task.Run</c> of one run.
/// </summary>
public sealed class StructPlaceMatrixTests
{
    public enum Storage
    {
        InstanceField,
        StaticField,
        ArrayCell,
        NestedField,
        CaptureCell,
        Box,
        Local,
        ByValueParameter,
        CallResult
    }

    public enum Route
    {
        Direct,
        RefLocal,
        RefParameter,
        InParameter,
        RefReturn,
        Span,
        ConditionalRef
    }

    public enum Use
    {
        FieldRead,
        FieldWrite,
        FieldReadModifyWrite,
        MutatingCall,
        ReadonlyCall,
        CopyOut,
        StoreIn,
        WriteThroughReference,
        MutatingCallThroughReference,
        PropertyGet,
        PropertySet,
        PropertyReadModifyWrite,
        IndexerSet,
        EventAdd,
        MethodGroupDelegate
    }

    public enum Form
    {
        Plain,
        ReadonlyStorage,
        ReadonlyReference
    }

    public sealed record Cell(Storage Storage, Route Route, Use Use, Form Form)
    {
        public override string ToString() => $"{Storage}/{Route}/{Use}/{Form}";
    }

    /// <summary>The kinds of region an observed access can be on: the cell's storage (the holder, the static region, the closure or the
    /// box), the object every <c>Ref</c> holds, a box a method-group delegate makes, and any other.</summary>
    private const string STORAGE = "storage";
    private const string SHARED = "shared";
    private const string DELEGATE_BOX = "delegate-box";
    private const string OTHER = "other";

    /// <summary>The fields and the event of the struct: a path holding one of them reaches into a struct.</summary>
    private static readonly string[] STRUCT_MEMBERS = ["X", "Ref", "Changed"];

    private const string SETUP = "/*setup*/";

    private static readonly Lazy<IReadOnlyDictionary<Cell, Observed>> RUNS = new(() =>
        Cells().AsParallel().Select(cell => (Cell: cell, Observed: Run(cell))).ToDictionary(item => item.Cell, item => item.Observed));

    public static IEnumerable<Cell> Cells() =>
        from storage in Enum.GetValues<Storage>()
        from route in Enum.GetValues<Route>()
        from use in Enum.GetValues<Use>()
        from form in Enum.GetValues<Form>()
        let cell = new Cell(storage, route, use, form)
        where Excluded(cell) is null
        select cell;

    /// <summary>Why a combination is no cell, or null when it is one.</summary>
    /// <param name="cell">The combination.</param>
    private static string? Excluded(Cell cell)
    {
        var (storage, route, use, form) = cell;
        if (route == Route.Span && storage != Storage.ArrayCell)
            return "a span is over an array";
        if (route is Route.RefLocal or Route.RefParameter or Route.RefReturn or Route.ConditionalRef && storage is Storage.Box or Storage.CallResult)
            return "C# gives no reference to a box or a call result";
        if (storage == Storage.Box && route != Route.Direct)
            return "C# reaches a boxed struct only through its members or by unboxing a copy";
        if (storage == Storage.Box && use is Use.FieldRead or Use.FieldWrite or Use.FieldReadModifyWrite or Use.StoreIn or Use.WriteThroughReference)
            return "C# reaches a boxed struct's fields only through its members";
        if (use is Use.PropertySet or Use.PropertyReadModifyWrite or Use.IndexerSet && (form != Form.Plain || storage == Storage.CallResult))
            return "C# rejects a setter on a readonly value or a call's result";
        if (use == Use.EventAdd && (form != Form.Plain || storage == Storage.CallResult))
            return "the defensive copy there is what the mutating member call under those forms checks";
        if (form == Form.ReadonlyStorage && storage is not (Storage.InstanceField or Storage.StaticField or Storage.NestedField))
            return "C# has no such declaration";
        if (form == Form.ReadonlyStorage && route != Route.Direct)
            return "C# reaches readonly storage otherwise only through a readonly reference, which that form covers";
        if (form == Form.ReadonlyReference && route is Route.Direct or Route.RefParameter || form == Form.Plain && route == Route.InParameter)
            return "the form is the route's";
        if (use is Use.FieldWrite or Use.FieldReadModifyWrite or Use.StoreIn && form != Form.Plain)
            return "C# rejects writing a readonly value";
        if (use is Use.FieldWrite or Use.FieldReadModifyWrite or Use.StoreIn && storage == Storage.CallResult)
            return "C# rejects assigning to a value a call returns";
        return null;
    }

    private static bool IsCopy(Storage storage) => storage is Storage.Local or Storage.ByValueParameter or Storage.CallResult;

    /// <summary>The path from the storage's region to the struct.</summary>
    /// <param name="storage">The storage.</param>
    private static string[] StoragePath(Storage storage) => storage switch
    {
        Storage.InstanceField => ["_s"],
        Storage.StaticField => ["Static"],
        Storage.ArrayCell => ["_arr", "[0]"],
        Storage.NestedField => ["_outer", "Inner"],
        Storage.CaptureCell => ["s"],
        _ => []
    };

    /// <summary>The accesses a cell's use makes, each as its key.</summary>
    /// <param name="cell">The cell.</param>
    private static IReadOnlySet<string> Expected(Cell cell)
    {
        var expected = new SortedSet<string>(StringComparer.Ordinal);
        // The array keeps one cell. An access to a field names it `[0]` where the body making the access names the index — directly,
        // through a span, a `ref` local or a conditional `ref` — and `[?]` where it reaches the field through the struct's storage without
        // the index: in a member's body through `this`, in a helper's body through its parameter, through a reference a call returns. An
        // access to the whole struct goes through the reference to the cell, which carries the index the caller named across a `ref` or
        // `in` argument and a `ref` return: it names `[0]` on every route.
        var namesIndex = cell.Route is not (Route.RefParameter or Route.InParameter or Route.RefReturn);
        string[] At(bool named) => cell.Storage == Storage.ArrayCell ? ["_arr", named ? "[0]" : "[?]"] : StoragePath(cell.Storage);
        void On(string target, string[] path, AccessOperation operation) => expected.Add(Key(target, path, operation));
        void Whole(AccessOperation operation) => On(STORAGE, At(named: true), operation);
        void Field(string field, AccessOperation operation, bool inMember = false) => On(STORAGE, [.. At(namesIndex && !inMember), field], operation);

        if (cell.Use is Use.WriteThroughReference or Use.MutatingCallThroughReference)
            On(SHARED, ["Count"], AccessOperation.ReadModifyWrite);
        if (IsCopy(cell.Storage))
        {
            if (cell.Use == Use.MethodGroupDelegate)
                On(DELEGATE_BOX, ["X"], AccessOperation.ReadModifyWrite);
            return expected;
        }

        // A member that is not readonly runs on a defensive copy of readonly storage or of what a readonly reference names.
        var defensive = cell.Form != Form.Plain;
        switch (cell.Use)
        {
            case Use.FieldRead:
                Field("X", AccessOperation.Read);
                break;
            case Use.ReadonlyCall:
                Field("X", AccessOperation.Read, inMember: true);
                break;
            case Use.FieldWrite:
                Field("X", AccessOperation.Write);
                break;
            case Use.PropertySet or Use.IndexerSet:
                Field("X", AccessOperation.Write, inMember: true);
                break;
            case Use.FieldReadModifyWrite:
                Field("X", AccessOperation.ReadModifyWrite);
                break;
            // The getter's read and the setter's write of `.P++` are one read-modify-write, as a compound assignment of a property is.
            case Use.PropertyReadModifyWrite:
                Field("X", AccessOperation.ReadModifyWrite, inMember: true);
                break;
            case Use.MutatingCall:
                if (defensive)
                    Whole(AccessOperation.Read);
                else
                    Field("X", AccessOperation.ReadModifyWrite, inMember: true);
                break;
            case Use.CopyOut:
                Whole(AccessOperation.Read);
                break;
            case Use.StoreIn:
                Whole(AccessOperation.Write);
                break;
            case Use.WriteThroughReference:
                Field("Ref", AccessOperation.Read);
                break;
            case Use.MutatingCallThroughReference:
                if (defensive)
                    Whole(AccessOperation.Read);
                else
                    Field("Ref", AccessOperation.Read, inMember: true);
                break;
            case Use.PropertyGet:
                if (defensive)
                    Whole(AccessOperation.Read);
                else
                    Field("X", AccessOperation.Read, inMember: true);
                break;
            // A field-like event's add accessor swaps the backing field with Interlocked.CompareExchange.
            case Use.EventAdd:
                Field("Changed", AccessOperation.AtomicReadModifyWrite, inMember: true);
                break;
            case Use.MethodGroupDelegate:
                // Made from the box through the interface, the delegate binds the box itself; made from storage, it boxes a copy.
                if (cell.Storage == Storage.Box)
                {
                    Field("X", AccessOperation.ReadModifyWrite, inMember: true);
                }
                else
                {
                    Whole(AccessOperation.Read);
                    On(DELEGATE_BOX, ["X"], AccessOperation.ReadModifyWrite);
                }
                break;
        }

        return expected;
    }

    [Fact]
    public void Every_cell_accesses_as_expected()
    {
        var failures = new List<string>();
        foreach (var cell in Cells())
        {
            var observed = Observe(cell);
            var expected = Expected(cell);
            if (observed.Error is not null)
                failures.Add($"{cell}: {observed.Error}");
            else if (!observed.Accesses.SetEquals(expected))
                failures.Add($"{cell}: [{string.Join(", ", observed.Accesses)}], expected [{string.Join(", ", expected)}]");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} cells:\n{string.Join("\n", failures)}");
    }

    [Fact]
    public void No_cell_loses_an_access()
    {
        var failures = new List<string>();
        foreach (var cell in Cells())
        {
            var observed = Observe(cell);
            failures.AddRange(Expected(cell).Where(key => !observed.Accesses.Contains(key)).Select(key => $"{cell}: {key}"));
        }

        Assert.True(failures.Count == 0, $"{failures.Count} lost:\n{string.Join("\n", failures)}");
    }

    [Fact]
    public void No_copy_is_a_resource()
    {
        var failures = new List<string>();
        foreach (var cell in Cells().Where(cell => IsCopy(cell.Storage)))
        {
            // On a copy the only accesses are on the object its reference field holds and on a box a method-group delegate makes.
            failures.AddRange(Observe(cell).Accesses.Where(key => !key.StartsWith($"{SHARED}|", StringComparison.Ordinal) &&
                                                                  !key.StartsWith($"{DELEGATE_BOX}|", StringComparison.Ordinal))
                                               .Select(key => $"{cell}: {key}"));
        }

        Assert.True(failures.Count == 0, $"{failures.Count} accesses on copies:\n{string.Join("\n", failures)}");
    }

    [Fact]
    public void No_copy_loses_the_object_its_reference_field_holds()
    {
        var shared = Key(SHARED, ["Count"], AccessOperation.ReadModifyWrite);
        var failures = Cells().Where(cell => cell.Use is Use.WriteThroughReference or Use.MutatingCallThroughReference)
                              .Where(cell => !Observe(cell).Accesses.Contains(shared))
                              .Select(cell => cell.ToString())
                              .ToList();

        Assert.True(failures.Count == 0, $"{failures.Count} cells lost {shared}:\n{string.Join("\n", failures)}");
    }

    [Fact]
    public void No_method_group_delegate_writes_the_storage()
    {
        var failures = new List<string>();
        foreach (var cell in Cells().Where(cell => cell is { Use: Use.MethodGroupDelegate, Storage: not Storage.Box }))
        {
            var observed = Observe(cell);
            failures.AddRange(observed.Accesses.Where(key => key.StartsWith($"{STORAGE}|", StringComparison.Ordinal) &&
                                                             !key.EndsWith($"|{AccessOperation.Read.ToWireName()}", StringComparison.Ordinal))
                                      .Select(key => $"{cell}: {key}"));
            // Only the worker that made the delegate reaches its box: its invocations pair with nothing.
            if (observed.DelegateBoxPairs != 0)
                failures.Add($"{cell}: {observed.DelegateBoxPairs} pairs on the delegate's box");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var cells = Cells().ToArray();

        Assert.Equal(Enum.GetValues<Storage>(), cells.Select(cell => cell.Storage).Distinct().Order());
        Assert.Equal(Enum.GetValues<Route>(), cells.Select(cell => cell.Route).Distinct().Order());
        Assert.Equal(Enum.GetValues<Use>(), cells.Select(cell => cell.Use).Distinct().Order());
        Assert.Equal(Enum.GetValues<Form>(), cells.Select(cell => cell.Form).Distinct().Order());
        // Each readonly form and each route meets heap storage, where a defensive copy and a reference to storage show.
        Assert.All(new[] { Form.ReadonlyStorage, Form.ReadonlyReference },
                   form => Assert.Contains(cells, cell => cell.Form == form && cell.Use == Use.MutatingCall && !IsCopy(cell.Storage)));
        Assert.All(Enum.GetValues<Route>(),
                   route => Assert.Contains(cells, cell => cell.Route == route && !IsCopy(cell.Storage) && cell.Storage != Storage.Box));
    }

    // ---- the fixture of a cell ----

    /// <summary>A cell's accesses as keys, its pairs on a box a method-group delegate makes, and the error that kept it from running.</summary>
    /// <param name="Accesses">The keys of the accesses the cell's use makes.</param>
    /// <param name="DelegateBoxPairs">How many pairs are on a box a method-group delegate made.</param>
    /// <param name="Error">What kept the cell from running, or null.</param>
    private sealed record Observed(IReadOnlySet<string> Accesses, int DelegateBoxPairs, string? Error);

    /// <summary>An access as a key, its selector as the access names it: <c>[0]</c> where the body making it names the index, <c>[?]</c>
    /// where it reaches the cell without it (<see cref="Expected"/>).</summary>
    /// <param name="target">The kind of region the access is on.</param>
    /// <param name="path">The access path.</param>
    /// <param name="operation">The operation.</param>
    private static string Key(string target, IEnumerable<string> path, AccessOperation operation) =>
        $"{target}|{string.Join(".", path)}|{operation.ToWireName()}";

    private static Observed Observe(Cell cell) => RUNS.Value[cell];

    private static Observed Run(Cell cell)
    {
        try
        {
            return Observe(cell, Source(cell));
        }
        catch (Exception exception)
        {
            return new Observed(new SortedSet<string>(StringComparer.Ordinal), 0, exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>The accesses of a cell's use: every access but the setup's, on its storage below the struct, on the shared object, on a
    /// box of the struct, or anywhere else whose path reaches into a struct.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="source">The cell's source.</param>
    private static Observed Observe(Cell cell, string source)
    {
        var run = Analyze(source);
        var heap = run.Execution.Heap;
        var lines = (Usings + source).Split('\n');
        var setup = Enumerable.Range(0, lines.Length).Where(line => lines[line].Contains(SETUP, StringComparison.Ordinal)).Select(line => line + 1).ToHashSet();
        var shared = Assert.Single(heap.Heap.Regions.Values, region => region.TypeKey == "Fixture:Item").Identity;
        var holder = run.Execution.Heap.Region("di:Holder@Singleton");
        var storage = cell.Storage switch
        {
            Storage.InstanceField or Storage.ArrayCell or Storage.NestedField => holder.Identity,
            Storage.StaticField => heap.Region("static:Holder").Identity,
            Storage.CaptureCell => heap.Region("closure:Holder.Use()").Identity,
            Storage.Box => Assert.Single(heap.Heap.PointsTo(holder.Identity, "Fixture:Holder._boxed")),
            _ => null
        };
        var boxes = heap.Heap.Regions.Values.Where(region => region.TypeKey == "Fixture:S" && region.Identity != storage).Select(region => region.Identity)
                        .ToHashSet(StringComparer.Ordinal);
        var whole = StoragePath(cell.Storage);

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var access in run.Collection.Accesses.Where(access => !access.IsConstructionLocal && !setup.Contains(access.Source.StartLine)))
        {
            var path = access.Resource.AccessPath;
            var region = access.Resource.RegionId;
            // On the storage's region every path through the field that keeps the struct counts, the array's own load aside.
            var target = region is not null && region == storage &&
                         (whole.Length == 0 || path.FirstOrDefault() == whole[0] && !(cell.Storage == Storage.ArrayCell && path.Count == 1)) ? STORAGE
                : region == shared ? SHARED
                : region is not null && boxes.Contains(region) ? DELEGATE_BOX
                : path.Any(segment => STRUCT_MEMBERS.Contains(segment)) ? OTHER
                : null;
            if (target is not null)
                keys.Add(Key(target, path, access.Operation));
        }

        var boxPairs = run.Pairs.Pairs.Count(pair => pair.Resource.RegionId is { } region && boxes.Contains(region));
        return new Observed(keys, boxPairs, null);
    }

    /// <summary>The struct's place, as the use names it.</summary>
    /// <param name="storage">The storage.</param>
    private static string Place(Storage storage) => storage switch
    {
        Storage.InstanceField => "_s",
        Storage.StaticField => "Static",
        Storage.ArrayCell => "_arr[0]",
        Storage.NestedField => "_outer.Inner",
        Storage.CaptureCell => "s",
        Storage.Box => "_boxed",
        Storage.Local => "local",
        Storage.ByValueParameter => "p",
        _ => "Make()"
    };

    /// <summary>The statements of a use on a struct expression.</summary>
    /// <param name="use">The use.</param>
    /// <param name="target">The expression naming the struct.</param>
    /// <param name="box">Whether the expression is the box behind the interface.</param>
    private static string Statements(Use use, string target, bool box) => use switch
    {
        Use.FieldRead => $"_ = {target}.X;",
        Use.FieldWrite => $"{target}.X = 1;",
        Use.FieldReadModifyWrite => $"{target}.X++;",
        Use.MutatingCall => $"{target}.Mutate();",
        Use.ReadonlyCall => $"_ = {target}.Peek();",
        Use.CopyOut => box ? $"S copy = (S){target};\n_ = copy;" : $"S copy = {target};\n_ = copy;",
        Use.StoreIn => $"{target} = new S {{ Ref = _shared }};",
        Use.WriteThroughReference => $"{target}.Ref.Count++;",
        Use.MutatingCallThroughReference => $"{target}.Touch();",
        Use.PropertyGet => $"_ = {target}.P;",
        Use.PropertySet => $"{target}.P = 1;",
        Use.PropertyReadModifyWrite => $"{target}.P++;",
        Use.IndexerSet => $"{target}[0] = 1;",
        Use.EventAdd => $"{target}.Changed += Handler;",
        _ => $"Action a = {target}.Mutate;\na();\na();"
    };

    /// <summary>The statements that reach the cell's place by its route and use it there.</summary>
    /// <param name="cell">The cell.</param>
    private static string Routed(Cell cell)
    {
        var place = Place(cell.Storage);
        var readonlyReference = cell.Form == Form.ReadonlyReference;
        var reference = readonlyReference ? "ref readonly" : "ref";
        string UseOn(string target) => Statements(cell.Use, target, cell.Storage == Storage.Box);
        return cell.Route switch
        {
            Route.Direct => UseOn(place),
            Route.RefLocal => $"{reference} S r = ref {place};\n{UseOn("r")}",
            Route.RefParameter => $"Act(ref {place});",
            Route.InParameter => cell.Storage == Storage.CallResult ? "ActIn(Make());" : $"ActIn(in {place});",
            Route.RefReturn => UseOn(readonlyReference ? $"ThroughIn(in {place})" : $"Through(ref {place})"),
            Route.Span => $"{(readonlyReference ? "ReadOnlySpan" : "Span")}<S> span = _arr;\n{UseOn("span[0]")}",
            _ => "var flag = Environment.ProcessorCount > 1;\n" +
                 $"var other = new S {{ Ref = _shared }}; {SETUP}\n" +
                 (readonlyReference ? $"ref readonly S r = ref (flag ? ref {place} : ref other);\n{UseOn("r")}" : UseOn($"(flag ? ref {place} : ref other)"))
        };
    }

    private static string Source(Cell cell)
    {
        var isStatic = cell.Storage == Storage.StaticField;
        var declared = cell.Form == Form.ReadonlyStorage ? "readonly " : "";
        var (declaration, initialization) = cell.Storage switch
        {
            Storage.InstanceField => ($"private {declared}S _s;", "_s = new S { Ref = _shared };"),
            Storage.StaticField => ($"private static {declared}S Static = new S {{ Ref = _shared }}; {SETUP}", ""),
            Storage.ArrayCell => ("private readonly S[] _arr;", "_arr = new[] { new S { Ref = _shared } };"),
            Storage.NestedField => ($"private {declared}Outer _outer;", "_outer = new Outer { Inner = new S { Ref = _shared } };"),
            Storage.Box => ("private readonly IS _boxed;", "_boxed = new S { Ref = _shared };"),
            _ => ("", "")
        };
        var routed = Routed(cell);
        var use = cell.Storage switch
        {
            Storage.Local => $$"""
                public void Use()
                {
                    var local = new S { Ref = _shared }; {{SETUP}}
                    {{routed}}
                }
                """,
            Storage.ByValueParameter => $$"""
                public void Use() => UseParam(new S { Ref = _shared });

                private void UseParam(S p)
                {
                    {{routed}}
                }
                """,
            Storage.CaptureCell => $$"""
                public void Use()
                {
                    var s = new S { Ref = _shared }; {{SETUP}}
                    var first = Task.Run(() =>
                    {
                        {{routed}}
                    });
                    var second = Task.Run(() =>
                    {
                        {{routed}}
                    });
                    Task.WaitAll(first, second);
                }
                """,
            _ => $$"""
                public void Use()
                {
                    {{routed}}
                }
                """
        };
        // A helper holds the use only on its own route: an in parameter's body cannot hold a write another route makes.
        var helped = Statements(cell.Use, "r", box: false);
        var throughReference = cell.Route == Route.RefParameter ? helped : "";
        var throughIn = cell.Route == Route.InParameter ? helped : "";
        var workers = cell.Storage == Storage.CaptureCell
            ? "services.AddHostedService<FirstWorker>();"
            : "services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();";
        return $$"""
            public sealed class Item { public int Count; }

            public interface IS
            {
                int P { get; set; }
                int this[int i] { get; set; }
                event Action? Changed;
                void Mutate();
                int Peek();
                void Touch();
            }

            public struct S : IS
            {
                public int X;
                public Item Ref;
                public int P { get => X; set => X = value; }
                public int this[int i] { get => X; set => X = value; }
                public event Action? Changed;
                public void Mutate() { X++; }
                public readonly int Peek() => X;
                public void Touch() { Ref.Count++; }
            }

            public struct Outer { public S Inner; }

            public sealed class Holder
            {
                {{(isStatic ? $"private static readonly Item _shared = new Item(); {SETUP}" : "private readonly Item _shared;")}}
                {{declaration}}

                public Holder()
                {
                    {{(isStatic ? "" : $"_shared = new Item(); {SETUP}")}}
                    {{initialization}} {{SETUP}}
                }

                {{use}}

                private void Act(ref S r)
                {
                    {{throughReference}}
                }

                private void ActIn(in S r)
                {
                    {{throughIn}}
                }

                private static ref S Through(ref S s) => ref s;

                private static ref readonly S ThroughIn(in S s) => ref s;

                private S Make() => new S { Ref = _shared };

                private static void Handler() { }
            }

            public sealed class FirstWorker : BackgroundService
            {
                private readonly Holder _holder;
                public FirstWorker(Holder holder) { _holder = holder; }
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _holder.Use();
                    return Task.CompletedTask;
                }
            }

            public sealed class SecondWorker : BackgroundService
            {
                private readonly Holder _holder;
                public SecondWorker(Holder holder) { _holder = holder; }
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _holder.Use();
                    return Task.CompletedTask;
                }
            }

            """ + Startup($"services.AddSingleton<Holder>(); {workers}");
    }
}
