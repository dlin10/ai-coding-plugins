using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>
/// Every place a struct is kept in, reached by every route, used every way, end to end (R1, R2, R3). Heap storage — an instance field,
/// a static field, an array cell, a nested struct field, a capture cell, a captured parameter — is the holder region's storage, and a
/// use of it is the matching access on (holder region, path to the field), a member's body included through <c>this</c>; a readonly
/// declaration or a readonly reference makes a member that is not <c>readonly</c> run on a defensive copy, a read of the whole struct; a
/// box is a region of its own reached through its members; a local, a by-value parameter and a call's result are copies, no location at
/// all; the contents of a <c>Nullable&lt;S&gt;</c> or a <c>KeyValuePair&lt;int, S&gt;</c> field are a copy taken by a read of the whole
/// field. Through every copy the reference field still points at the singleton's one object, a conversion or a pattern boxes a copy, and
/// a method-group delegate boxes a copy that only its maker reaches.
/// Two hosted workers of one singleton perform the use, or, for a capture cell and a captured parameter, two <c>Task.Run</c> of one run.
/// <para>Two test classes run it, each over half of the <see cref="Storage"/> values (<see cref="StructPlaceMatrixTests"/>,
/// <see cref="StructPlaceMatrixCellTests"/>), and each keeps its half's runs in its own <see cref="ClassCache"/>.</para>
/// </summary>
/// <param name="cache">The test class's cache, which keeps the runs of its half.</param>
/// <param name="half">The storages the test class runs.</param>
/// <param name="routes">Every route the half can meet.</param>
/// <param name="uses">Every use the half can meet.</param>
/// <param name="forms">Every form the half can meet.</param>
public sealed class StructPlaceMatrix(ClassCache cache, IReadOnlyList<StructPlaceMatrix.Storage> half, IReadOnlyList<StructPlaceMatrix.Route> routes,
                                      IReadOnlyList<StructPlaceMatrix.Use> uses, IReadOnlyList<StructPlaceMatrix.Form> forms)
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
        CallResult,
        CapturedParameter,
        NullableField,
        NullableFieldDefault,
        PairField
    }

    public enum Route
    {
        Direct,
        RefLocal,
        RefParameter,
        InParameter,
        RefReturn,
        Span,
        ConditionalRef,
        RefProperty,
        RefIndexer,
        GenericRef,
        GenericIn,
        GenericByValue,
        RefForeach,
        OutParameter,
        ConditionalRefHeap,
        RefLocalLoop
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
        MethodGroupDelegate,
        RefArgument,
        InArgument,
        OutArgument,
        Box,
        PatternBox,
        With,
        Deconstruct,
        Foreach
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
    /// box), the second storage of a route that reaches two, the object every <c>Ref</c> holds, a box a conversion or a pattern makes, a
    /// box a method-group delegate makes, and any other.</summary>
    private const string STORAGE = "storage";
    private const string STORAGE2 = "storage2";
    private const string SHARED = "shared";
    private const string BOX = "box";
    private const string DELEGATE_BOX = "delegate-box";
    private const string OTHER = "other";

    /// <summary>The fields and the event of the struct: a path holding one of them reaches into a struct.</summary>
    private static readonly string[] STRUCT_MEMBERS = ["X", "Ref", "Changed"];

    private const string SETUP = "/*setup*/";

    /// <summary>The storages of <see cref="StructPlaceMatrixTests"/>: storage a field keeps, the box and the local, and the fields whose
    /// contents a <c>Nullable</c> or a pair hands out.</summary>
    public static readonly Storage[] FIELD_HELD = [Storage.InstanceField, Storage.StaticField, Storage.NestedField, Storage.Box, Storage.Local,
                                                   Storage.NullableField, Storage.NullableFieldDefault, Storage.PairField];

    /// <summary>The storages of <see cref="StructPlaceMatrixCellTests"/>: a cell, a capture cell, a parameter, a call's result and a
    /// captured parameter.</summary>
    public static readonly Storage[] CELLS = [Storage.ArrayCell, Storage.CaptureCell, Storage.ByValueParameter, Storage.CallResult,
                                              Storage.CapturedParameter];

    private IReadOnlyDictionary<Cell, Observed> Runs =>
        cache.Get("runs", () => Cells(half).AsParallel().Select(cell => (Cell: cell, Observed: Run(cell))).ToDictionary(item => item.Cell, item => item.Observed));

    /// <summary>The cells of some storages.</summary>
    /// <param name="storages">The storages.</param>
    public static IEnumerable<Cell> Cells(IEnumerable<Storage> storages) =>
        from storage in storages
        from route in Enum.GetValues<Route>()
        from use in Enum.GetValues<Use>()
        from form in Enum.GetValues<Form>()
        let cell = new Cell(storage, route, use, form)
        where Excluded(cell) is null
        select cell;

    private IEnumerable<Cell> Cells() => Cells(half);

    private static bool IsGeneric(Route route) => route is Route.GenericRef or Route.GenericIn or Route.GenericByValue;

    /// <summary>Whether a route reaches a second storage of the same kind beside the cell's.</summary>
    /// <param name="route">The route.</param>
    private static bool HasSecond(Route route) => route is Route.ConditionalRefHeap or Route.RefLocalLoop;

    /// <summary>Whether the use works on what a field hands out by value — a <c>Nullable</c>'s value or a pair's — rather than on storage.</summary>
    /// <param name="storage">The storage.</param>
    private static bool IsContents(Storage storage) => storage is Storage.NullableField or Storage.NullableFieldDefault or Storage.PairField;

    /// <summary>Why a combination is no cell, or null when it is one.</summary>
    /// <param name="cell">The combination.</param>
    private static string? Excluded(Cell cell)
    {
        var (storage, route, use, form) = cell;
        if (IsContents(storage) && route is not (Route.Direct or Route.GenericByValue))
            return "`.Value` is no variable C# can take a reference to, while ActGenericByValue(_n.Value) compiles";
        if (IsContents(storage) && use is Use.FieldWrite or Use.FieldReadModifyWrite or Use.PropertySet or Use.PropertyReadModifyWrite or Use.IndexerSet)
            return "CS1612: C# rejects modifying `.Value`, which is not a variable";
        if (IsContents(storage) && use is Use.RefArgument or Use.InArgument or Use.OutArgument)
            return "CS8156, CS1510: C# passes no reference to `.Value`, which is not a variable";
        if (use is Use.RefArgument or Use.OutArgument && form != Form.Plain)
            return "CS8329, CS0192: C# rejects a writable reference to readonly storage";
        if (use is Use.RefArgument or Use.InArgument or Use.OutArgument && storage == Storage.Box)
            return "the box's static type is the interface";
        if (use is Use.RefArgument or Use.OutArgument && storage == Storage.CallResult)
            return "C# gives no writable reference to a call's result";
        if (use is Use.Box or Use.PatternBox or Use.With && storage == Storage.Box)
            return "the value already is the box, and C# rejects `with` on an interface";
        if (use == Use.Foreach && (storage != Storage.ArrayCell || route is not (Route.Direct or Route.Span)))
            return "a single struct is no array, and enumerating a struct itself is out of scope";
        if (route is Route.Span or Route.RefForeach && storage != Storage.ArrayCell)
            return "a span is over an array";
        if (route is Route.RefProperty or Route.RefIndexer &&
            storage is not (Storage.InstanceField or Storage.StaticField or Storage.ArrayCell or Storage.NestedField))
            return "C# returns no reference to a captured variable, a local, a parameter by value, a call's result or a box";
        if (route is Route.RefLocal or Route.RefParameter or Route.RefReturn or Route.ConditionalRef or Route.GenericRef or Route.OutParameter
                or Route.ConditionalRefHeap or Route.RefLocalLoop && storage is Storage.Box or Storage.CallResult)
            return "C# gives no reference to a box or a call result";
        if (route == Route.GenericIn && storage == Storage.Box)
            return "CS0453: the interface is no struct type argument";
        if (storage == Storage.Box && route != Route.Direct)
            return "C# reaches a boxed struct only through its members or by unboxing a copy";
        if (storage == Storage.Box && use is Use.FieldRead or Use.FieldWrite or Use.FieldReadModifyWrite or Use.StoreIn or Use.WriteThroughReference)
            return "C# reaches a boxed struct's fields only through its members";
        if (IsGeneric(route) && use is Use.FieldRead or Use.FieldWrite or Use.FieldReadModifyWrite or Use.WriteThroughReference)
            return "a type parameter shows only the interface's members";
        if (IsGeneric(route) && use == Use.StoreIn)
            return "a generic body cannot name S";
        if (use is Use.PropertySet or Use.PropertyReadModifyWrite or Use.IndexerSet && (form != Form.Plain || storage == Storage.CallResult))
            return "C# rejects a setter on a readonly value or a call's result";
        if (form == Form.ReadonlyStorage && storage is not (Storage.InstanceField or Storage.StaticField or Storage.NestedField))
            return "C# has no such declaration";
        if (form == Form.ReadonlyStorage && route != Route.Direct)
            return "C# reaches readonly storage otherwise only through a readonly reference, which that form covers";
        if (form == Form.ReadonlyReference && route == Route.GenericByValue)
            return "a by-value parameter is no reference";
        if (form == Form.ReadonlyReference && route is Route.Direct or Route.RefParameter or Route.GenericRef or Route.OutParameter ||
            form == Form.Plain && route is Route.InParameter or Route.GenericIn)
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
        Storage.CapturedParameter => ["p"],
        Storage.NullableField or Storage.NullableFieldDefault => ["_n"],
        Storage.PairField => ["_p"],
        _ => []
    };

    /// <summary>The path from the storage's region to the second struct a route reaches (<see cref="HasSecond"/>).</summary>
    /// <param name="storage">The storage.</param>
    private static string[] SecondPath(Storage storage) => storage switch
    {
        Storage.InstanceField => ["_s2"],
        Storage.StaticField => ["Static2"],
        Storage.ArrayCell => ["_arr2", "[0]"],
        Storage.NestedField => ["_outer2", "Inner"],
        Storage.CaptureCell => ["s2"],
        Storage.CapturedParameter => ["p2"],
        _ => []
    };

    /// <summary>The accesses a box made of a copy, beside the reads that took the copy: the box's own region, through the member a
    /// conversion or a pattern calls on it or the method a method-group delegate binds.</summary>
    /// <param name="use">The use.</param>
    /// <param name="expected">The keys, which this adds to.</param>
    private static void ExpectedOnBox(Use use, ISet<string> expected)
    {
        if (use is Use.Box or Use.PatternBox)
            expected.Add(Key(BOX, ["X"], AccessOperation.ReadModifyWrite));
        else if (use == Use.MethodGroupDelegate)
            expected.Add(Key(DELEGATE_BOX, ["X"], AccessOperation.ReadModifyWrite));
    }

    /// <summary>The accesses a cell's use makes, each as its key.</summary>
    /// <param name="cell">The cell.</param>
    private static IReadOnlySet<string> Expected(Cell cell)
    {
        var expected = new SortedSet<string>(StringComparer.Ordinal);
        if (cell.Use is Use.WriteThroughReference or Use.MutatingCallThroughReference)
            expected.Add(Key(SHARED, ["Count"], AccessOperation.ReadModifyWrite));
        if (IsCopy(cell.Storage))
        {
            ExpectedOnBox(cell.Use, expected);
            return expected;
        }

        // What a Nullable or a pair hands out is a copy taken by a read of the whole field, whichever part the use names; storing
        // into the field writes it whole.
        if (IsContents(cell.Storage))
        {
            expected.Add(Key(STORAGE, StoragePath(cell.Storage), cell.Use == Use.StoreIn ? AccessOperation.Write : AccessOperation.Read));
            ExpectedOnBox(cell.Use, expected);
            return expected;
        }

        ExpectedOn(cell, STORAGE, StoragePath(cell.Storage), expected);
        if (HasSecond(cell.Route))
            ExpectedOn(cell, STORAGE2, SecondPath(cell.Storage), expected);
        return expected;
    }

    /// <summary>Adds the accesses a cell's use makes on one storage it reaches.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="target">The kind of region the storage is.</param>
    /// <param name="whole">The path from the storage's region to the struct.</param>
    /// <param name="expected">The keys, which this adds to.</param>
    private static void ExpectedOn(Cell cell, string target, string[] whole, ISet<string> expected)
    {
        // The array keeps one cell. An access to a field names it `[0]` where the body making the access names the index — directly,
        // through a span, a `ref` local or a conditional `ref` — and `[?]` where it reaches the field through the struct's storage without
        // the index: in a member's body through `this`, in a helper's body through its parameter, through a reference a call returns. An
        // access to the whole struct goes through the reference to the cell, which carries the index the caller named across a `ref`, `in`
        // or `out` argument and a `ref` return: it names `[0]` on every route but a `ref foreach`, where no body names the index.
        var namesIndex = cell.Route is not (Route.RefParameter or Route.InParameter or Route.RefReturn or Route.RefProperty or Route.RefIndexer
                                            or Route.GenericRef or Route.GenericIn or Route.OutParameter or Route.RefForeach);
        var wholeNamesIndex = cell.Route != Route.RefForeach;
        string[] At(bool named) => cell.Storage == Storage.ArrayCell ? [whole[0], named ? "[0]" : "[?]"] : whole;
        void On(string on, string[] path, AccessOperation operation) => expected.Add(Key(on, path, operation));
        void Whole(AccessOperation operation) => On(target, At(wholeNamesIndex), operation);
        void Field(string field, AccessOperation operation, bool inMember = false) => On(target, [.. At(namesIndex && !inMember), field], operation);

        // A by-value type parameter takes a copy at the call; every use inside works on that copy.
        if (cell.Route == Route.GenericByValue)
        {
            Whole(AccessOperation.Read);
            ExpectedOnBox(cell.Use, expected);
            return;
        }

        // The helper of an out parameter writes the whole struct through the reference before the use.
        if (cell.Route == Route.OutParameter)
            Whole(AccessOperation.Write);

        // A member that is not readonly runs on a defensive copy of readonly storage or of what a readonly reference names; through an
        // `in` type parameter every member is an interface member, none of them readonly.
        var defensive = cell.Form != Form.Plain;
        var interfaceDefensive = cell.Route == Route.GenericIn;
        switch (cell.Use)
        {
            case Use.FieldRead:
                Field("X", AccessOperation.Read);
                break;
            case Use.ReadonlyCall:
                if (interfaceDefensive)
                    Whole(AccessOperation.Read);
                else
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
            // A field-like event's add accessor swaps the backing field with Interlocked.CompareExchange; the accessor is not readonly.
            case Use.EventAdd:
                if (defensive)
                    Whole(AccessOperation.Read);
                else
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
            // Inc(ref s) => s.Mutate(): the helper's call works on the storage through the reference.
            case Use.RefArgument:
                Field("X", AccessOperation.ReadModifyWrite, inMember: true);
                break;
            // Read(in s) => s.Peek(): a readonly member reads through the reference; a type parameter's Peek is an interface member, not
            // readonly, so the generic helper calls it on a defensive copy.
            case Use.InArgument:
                if (IsGeneric(cell.Route))
                    Whole(AccessOperation.Read);
                else
                    Field("X", AccessOperation.Read, inMember: true);
                break;
            case Use.OutArgument:
                Whole(AccessOperation.Write);
                break;
            // A box holds a copy of the whole struct; the interface's member works on the box.
            case Use.Box or Use.PatternBox:
                Whole(AccessOperation.Read);
                On(BOX, ["X"], AccessOperation.ReadModifyWrite);
                break;
            case Use.With:
                Whole(AccessOperation.Read);
                break;
            // Deconstruct is a member that is not readonly: it increments X, then gives back X and Ref.
            case Use.Deconstruct:
                if (defensive || interfaceDefensive)
                {
                    Whole(AccessOperation.Read);
                }
                else
                {
                    Field("X", AccessOperation.ReadModifyWrite, inMember: true);
                    Field("X", AccessOperation.Read, inMember: true);
                    Field("Ref", AccessOperation.Read, inMember: true);
                }
                break;
            // A foreach by value copies each cell out; no body names the index.
            case Use.Foreach:
                On(target, [whole[0], "[?]"], AccessOperation.Read);
                break;
        }
    }

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

    public void No_copy_is_a_resource()
    {
        // On a copy the only accesses are on the object its reference field holds and on a box made of it. What a Nullable or a pair
        // hands out and what a by-value type parameter takes are copies too, taken by a read of the whole storage: below it, nothing.
        static bool OffCopy(string key) => key.StartsWith($"{SHARED}|", StringComparison.Ordinal) || key.StartsWith($"{BOX}|", StringComparison.Ordinal) ||
                                           key.StartsWith($"{DELEGATE_BOX}|", StringComparison.Ordinal);
        var failures = new List<string>();
        foreach (var cell in Cells().Where(cell => IsCopy(cell.Storage) || IsContents(cell.Storage) || cell.Route == Route.GenericByValue))
        {
            var taken = IsCopy(cell.Storage) ? null : $"{STORAGE}|{string.Join(".", StoragePath(cell.Storage))}|";
            failures.AddRange(Observe(cell).Accesses.Where(key => !OffCopy(key) && (taken is null || !key.StartsWith(taken, StringComparison.Ordinal)))
                                               .Select(key => $"{cell}: {key}"));
        }

        Assert.True(failures.Count == 0, $"{failures.Count} accesses on copies:\n{string.Join("\n", failures)}");
    }

    public void No_copy_loses_the_object_its_reference_field_holds()
    {
        var shared = Key(SHARED, ["Count"], AccessOperation.ReadModifyWrite);
        var failures = Cells().Where(cell => cell.Use is Use.WriteThroughReference or Use.MutatingCallThroughReference)
                              .Where(cell => !Observe(cell).Accesses.Contains(shared))
                              .Select(cell => cell.ToString())
                              .ToList();

        Assert.True(failures.Count == 0, $"{failures.Count} cells lost {shared}:\n{string.Join("\n", failures)}");
    }

    public void No_method_group_delegate_writes_the_storage()
    {
        var failures = new List<string>();
        foreach (var cell in Cells().Where(cell => cell is { Use: Use.MethodGroupDelegate, Storage: not Storage.Box }))
        {
            var observed = Observe(cell);
            // The helper of an out parameter writes the whole storage before it makes the delegate; the delegate writes none of it.
            var helperWrite = cell.Route == Route.OutParameter ? Key(STORAGE, StoragePath(cell.Storage), AccessOperation.Write) : null;
            failures.AddRange(observed.Accesses.Where(key => key.StartsWith(STORAGE, StringComparison.Ordinal) && key != helperWrite &&
                                                             !key.EndsWith($"|{AccessOperation.Read.ToWireName()}", StringComparison.Ordinal))
                                      .Select(key => $"{cell}: {key}"));
            // Only the worker that made the delegate reaches its box: its invocations pair with nothing.
            if (observed.DelegateBoxPairs != 0)
                failures.Add($"{cell}: {observed.DelegateBoxPairs} pairs on the delegate's box");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>Applicable event additions are maintained cells, including values returned from storage and calls.</summary>
    public void Event_add_on_contents_and_call_results_is_a_cell()
    {
        foreach (var storage in half.Where(storage => IsContents(storage) || storage == Storage.CallResult))
            Assert.Contains(new Cell(storage, Route.Direct, Use.EventAdd, Form.Plain), Cells());
        // Generic in on a call result is an applicable temporary, not an exclusion represented by another route.
        if (half.Contains(Storage.CallResult))
            Assert.Contains(new Cell(Storage.CallResult, Route.GenericIn, Use.MutatingCall, Form.ReadonlyReference), Cells());
    }

    public void Every_value_of_every_axis_occurs()
    {
        // Both halves together meet every value of every axis.
        var cells = Cells(Enum.GetValues<Storage>()).ToArray();
        Assert.Equal(Enum.GetValues<Storage>(), cells.Select(cell => cell.Storage).Distinct().Order());
        Assert.Equal(Enum.GetValues<Route>(), cells.Select(cell => cell.Route).Distinct().Order());
        Assert.Equal(Enum.GetValues<Use>(), cells.Select(cell => cell.Use).Distinct().Order());
        Assert.Equal(Enum.GetValues<Form>(), cells.Select(cell => cell.Form).Distinct().Order());
        Assert.Equal(Enum.GetValues<Storage>().Order(), FIELD_HELD.Concat(CELLS).Order());
        Assert.Empty(FIELD_HELD.Intersect(CELLS));
        // Each readonly form and each route meets heap storage, where a defensive copy and a reference to storage show.
        Assert.All(new[] { Form.ReadonlyStorage, Form.ReadonlyReference },
                   form => Assert.Contains(cells, cell => cell.Form == form && cell.Use == Use.MutatingCall && !IsCopy(cell.Storage)));
        Assert.All(Enum.GetValues<Route>(),
                   route => Assert.Contains(cells, cell => cell.Route == route && !IsCopy(cell.Storage) && cell.Storage != Storage.Box));

        // This half meets every route, use and form it can meet.
        var own = Cells().ToArray();
        Assert.Equal(half.Order(), own.Select(cell => cell.Storage).Distinct().Order());
        Assert.Equal(routes.Order(), own.Select(cell => cell.Route).Distinct().Order());
        Assert.Equal(uses.Order(), own.Select(cell => cell.Use).Distinct().Order());
        Assert.Equal(forms.Order(), own.Select(cell => cell.Form).Distinct().Order());
    }

    // ---- the fixture of a cell ----

    /// <summary>A cell's accesses as keys, its pairs on a box a method-group delegate makes, and the error that kept it from running.</summary>
    /// <param name="Accesses">The keys of the accesses the cell's use makes.</param>
    /// <param name="DelegateBoxPairs">How many pairs are on a box a method-group delegate made.</param>
    /// <param name="Error">What kept the cell from running, or null.</param>
    private sealed record Observed(IReadOnlySet<string> Accesses, int DelegateBoxPairs, string? Error);

    /// <summary>An access as a key, its selector as the access names it: <c>[0]</c> where the body making it names the index, <c>[?]</c>
    /// where it reaches the cell without it (<see cref="ExpectedOn"/>).</summary>
    /// <param name="target">The kind of region the access is on.</param>
    /// <param name="path">The access path.</param>
    /// <param name="operation">The operation.</param>
    private static string Key(string target, IEnumerable<string> path, AccessOperation operation) =>
        $"{target}|{string.Join(".", path)}|{operation.ToWireName()}";

    private Observed Observe(Cell cell) => Runs[cell];

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

    /// <summary>The accesses of a cell's use: every access but the setup's, on its storage below the struct, on the second storage a
    /// route reaches, on the shared object, on a box of the struct, or anywhere else whose path reaches into a struct.</summary>
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
            Storage.InstanceField or Storage.ArrayCell or Storage.NestedField or Storage.NullableField or Storage.NullableFieldDefault or Storage.PairField =>
                holder.Identity,
            Storage.StaticField => heap.Region("static:Holder").Identity,
            Storage.CaptureCell => heap.Region("closure:Holder.Use()").Identity,
            Storage.CapturedParameter => heap.Region(HasSecond(cell.Route) ? "closure:Holder.UseParam(S, S)" : "closure:Holder.UseParam(S)").Identity,
            Storage.Box => Assert.Single(heap.Heap.PointsTo(holder.Identity, "Fixture:Holder._boxed")),
            _ => null
        };
        // A box is named by the operation that made it: a method-group delegate's when a delegate binds the box, a conversion's or a
        // pattern's otherwise.
        var bodies = heap.Program.Result.Bodies;
        bool MadeForDelegate(HeapRegion box) =>
            box.SiteBodyId is { } body && bodies.TryGetValue(body, out var lowered) &&
            lowered.Blocks.SelectMany(block => block.Operations).OfType<IrBoxOperation>().FirstOrDefault(operation => operation.Id == box.SiteOperationId) is { } made &&
            lowered.Blocks.SelectMany(block => block.Operations).OfType<IrCreateDelegateOperation>().Any(created => created.ReceiverValue == made.ResultValue);
        var madeBoxes = heap.Heap.Regions.Values.Where(region => region.TypeKey == "Fixture:S" && region.Identity != storage).ToArray();
        var boxes = madeBoxes.Where(region => !MadeForDelegate(region)).Select(region => region.Identity).ToHashSet(StringComparer.Ordinal);
        var delegateBoxes = madeBoxes.Where(MadeForDelegate).Select(region => region.Identity).ToHashSet(StringComparer.Ordinal);
        var whole = StoragePath(cell.Storage);
        var second = HasSecond(cell.Route) ? SecondPath(cell.Storage) : [];

        // On a storage's region every path through the field that keeps the struct counts, the array's own load aside.
        bool Through(string[] path, IReadOnlyList<string> accessPath) =>
            path.Length != 0 && accessPath.FirstOrDefault() == path[0] && !(cell.Storage == Storage.ArrayCell && accessPath.Count == 1);

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var access in run.Collection.Accesses.Where(access => !access.IsConstructionLocal && !setup.Contains(access.Source.StartLine)))
        {
            var path = access.Resource.AccessPath;
            var region = access.Resource.RegionId;
            var onStorage = region is not null && region == storage;
            var target = onStorage && (whole.Length == 0 || Through(whole, path)) ? STORAGE
                : onStorage && Through(second, path) ? STORAGE2
                : region == shared ? SHARED
                : region is not null && boxes.Contains(region) ? BOX
                : region is not null && delegateBoxes.Contains(region) ? DELEGATE_BOX
                : path.Any(segment => STRUCT_MEMBERS.Contains(segment)) ? OTHER
                : null;
            if (target is not null)
                keys.Add(Key(target, path, access.Operation));
        }

        var boxPairs = run.Pairs.Pairs.Count(pair => pair.Resource.RegionId is { } region && delegateBoxes.Contains(region));
        return new Observed(keys, boxPairs, null);
    }

    /// <summary>The struct's place, as the use names it. A <c>Nullable</c> field is boxed, matched by a pattern and stored into itself,
    /// and a pair field deconstructed and stored into itself; every other use, and a by-value type parameter, takes their value.</summary>
    /// <param name="storage">The storage.</param>
    /// <param name="use">The use.</param>
    /// <param name="route">The route.</param>
    private static string Place(Storage storage, Use use = Use.FieldRead, Route route = Route.Direct) => storage switch
    {
        Storage.InstanceField => "_s",
        Storage.StaticField => "Static",
        Storage.ArrayCell => "_arr[0]",
        Storage.NestedField => "_outer.Inner",
        Storage.CaptureCell => "s",
        Storage.Box => "_boxed",
        Storage.Local => "local",
        Storage.ByValueParameter or Storage.CapturedParameter => "p",
        Storage.NullableField => route == Route.Direct && use is Use.Box or Use.PatternBox or Use.StoreIn ? "_n" : "_n.Value",
        Storage.NullableFieldDefault => route == Route.Direct && use == Use.StoreIn ? "_n" : "_n.GetValueOrDefault()",
        Storage.PairField => route == Route.Direct && use is Use.Deconstruct or Use.StoreIn ? "_p" : "_p.Value",
        _ => "Make()"
    };

    /// <summary>The second struct's place a route reaches (<see cref="HasSecond"/>), as the use names it: a storage of the same kind on
    /// the same holder.</summary>
    /// <param name="storage">The storage.</param>
    private static string SecondPlace(Storage storage) => storage switch
    {
        Storage.InstanceField => "_s2",
        Storage.StaticField => "Static2",
        Storage.ArrayCell => "_arr2[0]",
        Storage.NestedField => "_outer2.Inner",
        Storage.CaptureCell => "s2",
        Storage.Local => "local2",
        _ => "p2"
    };

    /// <summary>The statements of a use on a struct expression.</summary>
    /// <param name="use">The use.</param>
    /// <param name="target">The expression naming the struct.</param>
    /// <param name="box">Whether the expression is the box behind the interface.</param>
    /// <param name="type">The struct's type as the body names it: <c>S</c>, or a type parameter in a generic helper.</param>
    /// <param name="value">Whether the expression is a value rather than a variable, which C# passes to an <c>in</c> parameter without
    /// the modifier.</param>
    private static string Statements(Use use, string target, bool box, string type = "S", bool value = false) => use switch
    {
        Use.RefArgument => $"Inc(ref {target});",
        Use.InArgument => value ? $"_ = Read({target});" : $"_ = Read(in {target});",
        Use.OutArgument => $"Init(out {target});",
        Use.Box => $"object o = {target};\n((IS)o).Mutate();",
        Use.PatternBox => $"if ({target} is IS i)\n    i.Mutate();",
        // A type parameter shows only the interface's members, whose property a `with` sets as it sets a field of S.
        Use.With => type == "S" ? $"var c = {target} with {{ X = 1 }};\n_ = c;" : $"var c = {target} with {{ P = 1 }};\n_ = c;",
        Use.Deconstruct => $"var (a, b) = {target};",
        Use.Foreach => $"foreach (var x in {target})\n    x.Mutate();",
        Use.FieldRead => $"_ = {target}.X;",
        Use.FieldWrite => $"{target}.X = 1;",
        Use.FieldReadModifyWrite => $"{target}.X++;",
        Use.MutatingCall => $"{target}.Mutate();",
        Use.ReadonlyCall => $"_ = {target}.Peek();",
        Use.CopyOut => box ? $"{type} copy = ({type}){target};\n_ = copy;" : $"{type} copy = {target};\n_ = copy;",
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
        var place = Place(cell.Storage, cell.Use, cell.Route);
        var second = SecondPlace(cell.Storage);
        var readonlyReference = cell.Form == Form.ReadonlyReference;
        var reference = readonlyReference ? "ref readonly" : "ref";
        string UseOn(string target) => Statements(cell.Use, target, cell.Storage == Storage.Box, value: target == "Make()");
        // A foreach enumerates the array, directly or through a span over it; a pair field is stored into with a pair.
        if (cell.Use == Use.Foreach)
            return cell.Route == Route.Span ? $"{(readonlyReference ? "ReadOnlySpan" : "Span")}<S> span = _arr;\n{UseOn("span")}" : UseOn("_arr");
        if (cell is { Storage: Storage.PairField, Use: Use.StoreIn })
            return "_p = new System.Collections.Generic.KeyValuePair<int, S>(0, new S { Ref = _shared });";
        return cell.Route switch
        {
            Route.Direct => UseOn(place),
            Route.RefLocal => $"{reference} S r = ref {place};\n{UseOn("r")}",
            Route.RefParameter => $"Act(ref {place});",
            Route.InParameter => cell.Storage == Storage.CallResult ? "ActIn(Make());" : $"ActIn(in {place});",
            Route.RefReturn => UseOn(readonlyReference ? $"ThroughIn(in {place})" : $"Through(ref {place})"),
            Route.Span => $"{(readonlyReference ? "ReadOnlySpan" : "Span")}<S> span = _arr;\n{UseOn("span[0]")}",
            Route.RefProperty => UseOn("Slot"),
            Route.RefIndexer => UseOn("this[0]"),
            Route.GenericRef => $"ActGeneric(ref {place});",
            Route.GenericIn => cell.Storage == Storage.CallResult ? "ActGenericIn(Make());" : $"ActGenericIn(in {place});",
            Route.GenericByValue => $"ActGenericByValue({place});",
            Route.RefForeach => $"foreach ({reference} var x in ({(readonlyReference ? "ReadOnlySpan" : "Span")}<S>)_arr)\n{{\n{UseOn("x")}\n}}",
            Route.OutParameter => $"Fill(out {place});",
            Route.ConditionalRefHeap => "var flag = Environment.ProcessorCount > 1;\n" +
                                        (readonlyReference
                                            ? $"ref readonly S r = ref (flag ? ref {place} : ref {second});\n{UseOn("r")}"
                                            : UseOn($"(flag ? ref {place} : ref {second})")),
            Route.RefLocalLoop => $"{reference} S r = ref {place};\nfor (var turn = 0; turn < 2; turn++)\n{{\n{UseOn("r")}\nr = ref {second};\n}}",
            _ => "var flag = Environment.ProcessorCount > 1;\n" +
                 $"var other = new S {{ Ref = _shared }}; {SETUP}\n" +
                 (readonlyReference ? $"ref readonly S r = ref (flag ? ref {place} : ref other);\n{UseOn("r")}" : UseOn($"(flag ? ref {place} : ref other)"))
        };
    }

    /// <summary>The members a route declares on <c>Holder</c> beside the use: a reference-returning property or indexer, a generic
    /// helper or the helper of an out parameter. Every other cell declares none, so its source stays as it was.</summary>
    /// <param name="cell">The cell.</param>
    private static string RouteMembers(Cell cell)
    {
        var reference = cell.Form == Form.ReadonlyReference ? "ref readonly" : "ref";
        string Generic(string parameter) => $$"""
            private static void {{parameter}} where T : struct, IS
            {
                {{Statements(cell.Use, "x", box: false, type: "T")}}
            }
            """;
        return cell.Route switch
        {
            Route.RefProperty => $"private {reference} S Slot => ref {Place(cell.Storage)};",
            Route.RefIndexer => $"private {reference} S this[int i] => ref {Place(cell.Storage)};",
            Route.GenericRef => Generic("ActGeneric<T>(ref T x)"),
            Route.GenericIn => Generic("ActGenericIn<T>(in T x)"),
            Route.GenericByValue => Generic("ActGenericByValue<T>(T x)"),
            Route.OutParameter => $$"""
                private void Fill(out S s)
                {
                    s = new S { Ref = _shared };
                    {{Statements(cell.Use, "s", box: false)}}
                }
                """,
            _ => ""
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
            Storage.NullableField or Storage.NullableFieldDefault => ("private S? _n;", "_n = new S { Ref = _shared };"),
            Storage.PairField => ("private System.Collections.Generic.KeyValuePair<int, S> _p;",
                                  "_p = new System.Collections.Generic.KeyValuePair<int, S>(0, new S { Ref = _shared });"),
            _ => ("", "")
        };
        // A route that reaches a second storage declares it beside the first, of the same kind and holding the same shared item.
        var hasSecond = HasSecond(cell.Route);
        var (declaration2, initialization2) = !hasSecond ? ("", "") : cell.Storage switch
        {
            Storage.InstanceField => ("private S _s2;", "_s2 = new S { Ref = _shared };"),
            Storage.StaticField => ($"private static S Static2 = new S {{ Ref = _shared }}; {SETUP}", ""),
            Storage.ArrayCell => ("private readonly S[] _arr2;", "_arr2 = new[] { new S { Ref = _shared } };"),
            Storage.NestedField => ("private Outer _outer2;", "_outer2 = new Outer { Inner = new S { Ref = _shared } };"),
            _ => ("", "")
        };
        var routed = Routed(cell);
        var use = cell.Storage switch
        {
            Storage.Local => $$"""
                public void Use()
                {
                    var local = new S { Ref = _shared }; {{SETUP}}
                    {{(hasSecond ? $"var local2 = new S {{ Ref = _shared }}; {SETUP}" : "")}}
                    {{routed}}
                }
                """,
            Storage.ByValueParameter when hasSecond => $$"""
                public void Use() => UseParam(new S { Ref = _shared }, new S { Ref = _shared });

                private void UseParam(S p, S p2)
                {
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
                    {{(hasSecond ? $"var s2 = new S {{ Ref = _shared }}; {SETUP}" : "")}}
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
            Storage.CapturedParameter => $$"""
                public void Use() => UseParam({{(hasSecond ? "new S { Ref = _shared }, new S { Ref = _shared }" : "new S { Ref = _shared }")}});

                private void UseParam({{(hasSecond ? "S p, S p2" : "S p")}})
                {
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
        var workers = cell.Storage is Storage.CaptureCell or Storage.CapturedParameter
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
                void Deconstruct(out int x, out Item r);
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
                public void Deconstruct(out int x, out Item r) { X++; x = X; r = Ref; }
            }

            public struct Outer { public S Inner; }

            public sealed class Holder
            {
                {{(isStatic ? $"private static readonly Item _shared = new Item(); {SETUP}" : "private readonly Item _shared;")}}
                {{declaration}}
                {{declaration2}}

                public Holder()
                {
                    {{(isStatic ? "" : $"_shared = new Item(); {SETUP}")}}
                    {{initialization}} {{initialization2}} {{SETUP}}
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

                private static void Inc(ref S s) => s.Mutate();

                private static void Inc<T>(ref T s) where T : struct, IS => s.Mutate();

                private static int Read(in S s) => s.Peek();

                private static int Read<T>(in T s) where T : struct, IS => s.Peek();

                private void Init(out S s) => s = new S { Ref = _shared };

                private static void Init<T>(out T s) where T : struct => s = default;

                {{RouteMembers(cell)}}

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
