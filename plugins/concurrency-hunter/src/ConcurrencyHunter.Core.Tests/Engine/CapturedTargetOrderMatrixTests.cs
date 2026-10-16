using System.Collections.Concurrent;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>An assignment whose target Roslyn takes before its value evaluates the target's receiver and each index once, before any
/// part of the value, and writes through the place they name (R8, TD-012): every kind of target, written by every form that has the
/// target taken first, with a reference value and with a whole struct. Read in the lowered IR of one member per cell.</summary>
public sealed class CapturedTargetOrderMatrixTests
{
    /// <summary>The assignment's target.</summary>
    public enum Target
    {
        /// <summary><c>Get().F</c>.</summary>
        Field,

        /// <summary><c>Get().P</c>, an automatic property.</summary>
        AutoProperty,

        /// <summary><c>Get().Q</c>, a property with accessor bodies.</summary>
        AccessorProperty,

        /// <summary><c>Get()[I()]</c>, an indexer with accessor bodies and no <c>ref</c> return.</summary>
        Indexer,

        /// <summary><c>At(Get(), I())</c>, a method returning <c>ref</c>.</summary>
        RefMethod,

        /// <summary><c>Get().Slot</c>, a property returning <c>ref</c>.</summary>
        RefProperty,

        /// <summary><c>Get().Cells[I()]</c>, an indexer returning <c>ref</c>.</summary>
        RefIndexer,

        /// <summary><c>GetArr()[I()]</c>.</summary>
        ArrayCell,

        /// <summary><c>GetSpan()[I()]</c>.</summary>
        SpanCell,

        /// <summary><c>Get().S.X</c> (an <c>int?</c>) or <c>Get().S.Inner</c> (a struct): a field of struct storage.</summary>
        StructFieldPath
    }

    /// <summary>The form of the assignment.</summary>
    public enum Form
    {
        /// <summary><c>t ??= V()</c>.</summary>
        CoalesceAssign,

        /// <summary><c>t = F() ? A() : B()</c>.</summary>
        Conditional,

        /// <summary><c>t = A() ?? B()</c>.</summary>
        Coalesce,

        /// <summary><c>t = new T { X = V() }</c>.</summary>
        ObjectInitializer,

        /// <summary><c>t += F() ? A() : B()</c>.</summary>
        CompoundBranching,

        /// <summary><c>(t, u) = (A(), B())</c>.</summary>
        DeconstructionTarget
    }

    /// <summary>What the assignment writes.</summary>
    public enum Value
    {
        /// <summary>An object of a class, or an <c>int?</c> for <see cref="Target.StructFieldPath"/>.</summary>
        Reference,

        /// <summary>A struct the analysis follows.</summary>
        WholeStruct
    }

    /// <summary>A cell of the matrix.</summary>
    /// <param name="Target">The assignment's target.</param>
    /// <param name="Form">The form of the assignment.</param>
    /// <param name="Value">What it writes.</param>
    private sealed record Cell(Target Target, Form Form, Value Value)
    {
        public override string ToString() => $"{Target}/{Form}/{Value}";
    }

    /// <summary>The calls the value makes.</summary>
    private static readonly string[] ValueCalls = ["F", "A", "B", "V"];

    private static readonly ConcurrentDictionary<Cell, Lazy<Lowered>> Bodies = new();

    private static IEnumerable<Cell> AllCells() =>
        from target in Enum.GetValues<Target>()
        from form in Enum.GetValues<Form>()
        from value in Enum.GetValues<Value>()
        select new Cell(target, form, value);

    private static IEnumerable<Cell> Cells() => AllCells().Where(cell => Excluded(cell) is null);

    /// <summary>Why C# rejects a cell, as the compiler's error and its reason, or null for a cell it accepts.</summary>
    /// <param name="cell">The cell.</param>
    private static (string Error, string Reason)? Excluded(Cell cell) => cell switch
    {
        { Form: Form.CoalesceAssign, Value: Value.WholeStruct } => ("CS0019", "`??=` needs a nullable or reference target"),
        { Form: Form.Coalesce, Value: Value.WholeStruct } => ("CS0019", "`??` needs a nullable or reference left operand"),
        { Form: Form.ObjectInitializer, Value: Value.Reference, Target: Target.StructFieldPath } => ("CS0117", "no initializer on a number"),
        _ => null
    };

    public static TheoryData<Target, Form, Value> Rows()
    {
        var rows = new TheoryData<Target, Form, Value>();
        foreach (var cell in Cells())
            rows.Add(cell.Target, cell.Form, cell.Value);
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Every_cell_evaluates_its_target_first_and_once(Target target, Form form, Value value)
    {
        var cell = new Cell(target, form, value);
        var body = Lower(cell);
        var values = body.Calls(ValueCalls);
        Assert.True(values.Length != 0, $"{cell}: the value makes no call\n{body.Text}");
        var firstValue = values.Min(body.Position);
        var lastValue = values.Max(body.Position);

        var receiver = Assert.Single(body.Calls(ReceiverCall(cell)));
        Assert.True(body.Position(receiver) < firstValue, $"{cell}: {ReceiverCall(cell)} runs after the value starts\n{body.Text}");
        var taken = body.Position(receiver);
        if (HasIndex(cell))
        {
            var index = Assert.Single(body.Calls("I"));
            Assert.True(taken < body.Position(index) && body.Position(index) < firstValue,
                        $"{cell}: I is not evaluated after the receiver and before the value\n{body.Text}");
            taken = body.Position(index);
        }

        if (ReferenceCall(cell) is { } handing)
        {
            var reference = Assert.Single(body.Calls(handing));
            Assert.True(taken < body.Position(reference) && body.Position(reference) < firstValue,
                        $"{cell}: {handing} does not hand back the reference before the value\n{body.Text}");
            taken = body.Position(reference);
        }

        if (Accessors(cell) is { } accessors)
        {
            var (getter, setter) = accessors;
            var getters = body.Calls(getter);
            Assert.True(getters.Length == (Reads(cell) ? 1 : 0), $"{cell}: {getters.Length} calls of {getter}\n{body.Text}");
            Assert.All(getters, call => Assert.True(taken < body.Position(call) && body.Position(call) < firstValue,
                                                    $"{cell}: {getter} is not called after the target is taken and before the value\n{body.Text}"));
            var set = Assert.Single(body.Calls(setter));
            Assert.True(body.Position(set) > lastValue, $"{cell}: {setter} runs before the value\n{body.Text}");
            return;
        }

        // Any other target is read where it is taken only by a form that reads it, and never before a store that writes it whole.
        var reads = Reads(body, cell);
        Assert.True(reads.Length == (Reads(cell) ? 1 : 0), $"{cell}: the target is read {reads.Length} times\n{body.Text}");
        Assert.All(reads, read => Assert.True(taken < body.Position(read) && body.Position(read) < firstValue,
                                              $"{cell}: the target is not read after it is taken and before the value\n{body.Text}"));
        var store = Assert.Single(Stores(body, cell));
        Assert.True(body.Position(store) > lastValue, $"{cell}: the store runs before the value\n{body.Text}");
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Every_cell_writes_the_place_its_target_named(Target target, Form form, Value value)
    {
        var cell = new Cell(target, form, value);
        var body = Lower(cell);
        var receiver = Assert.Single(body.Calls(ReceiverCall(cell))).ResultValue!.Value;
        int? index = HasIndex(cell) ? Assert.Single(body.Calls("I")).ResultValue : null;

        if (Accessors(cell) is { } accessors)
        {
            var (getter, setter) = accessors;
            var set = Assert.Single(body.Calls(setter));
            AssertPlace(body, cell, set.ReceiverValue, set.ArgumentValues.SkipLast(1), receiver, index);
            foreach (var get in body.Calls(getter))
                AssertPlace(body, cell, get.ReceiverValue, get.ArgumentValues, receiver, index);
            return;
        }

        var place = Place(body, cell, receiver, index);
        foreach (var operation in Stores(body, cell).Concat(Reads(body, cell)))
        {
            var named = operation switch
            {
                IrStoreFieldOperation store => store.ReceiverValue,
                IrLoadFieldOperation load => load.ReceiverValue,
                IrStoreReferenceOperation store => store.AddressValue,
                IrLoadReferenceOperation load => load.AddressValue,
                IrStoreElementOperation store => store.ReceiverValue,
                IrLoadElementOperation load => load.ReceiverValue,
                _ => throw new InvalidOperationException($"{cell}: {operation} is no store or read of the target")
            };
            Assert.True(named is int through && body.Origin(through) == place, $"{cell}: {operation} goes through another place\n{body.Text}");
            var indices = operation switch
            {
                IrStoreElementOperation store => store.IndexValues,
                IrLoadElementOperation load => load.IndexValues,
                _ => null
            };
            if (indices is not null)
                Assert.True(indices.Select(body.Origin).SequenceEqual([index!.Value]), $"{cell}: {operation} names another index\n{body.Text}");
        }
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var all = AllCells().ToArray();
        var cells = Cells().ToArray();
        Assert.Equal(120, all.Length);
        Assert.Equal(21, all.Count(cell => Excluded(cell) is not null));
        Assert.Equal(99, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.All(Enum.GetValues<Target>(), value => Assert.Contains(cells, cell => cell.Target == value));
        Assert.All(Enum.GetValues<Form>(), value => Assert.Contains(cells, cell => cell.Form == value));
        Assert.All(Enum.GetValues<Value>(), value => Assert.Contains(cells, cell => cell.Value == value));
        // Every target meets a form that reads it and one that only writes it, with both values.
        Assert.All(Enum.GetValues<Target>(), value =>
        {
            Assert.Contains(cells, cell => cell.Target == value && Reads(cell));
            Assert.Contains(cells, cell => cell.Target == value && !Reads(cell) && cell.Value == Value.Reference);
            Assert.Contains(cells, cell => cell.Target == value && !Reads(cell) && cell.Value == Value.WholeStruct);
        });
        // C# rejects each excluded cell with the error its reason names; the fixture refuses a source with errors, naming them.
        foreach (var cell in all.Where(cell => Excluded(cell) is not null))
        {
            var rejected = Assert.Throws<InvalidOperationException>(() => Compile(cell));
            Assert.True(rejected.Message.Contains($"error {Excluded(cell)!.Value.Error}:", StringComparison.Ordinal),
                        $"{cell}: C# does not reject it with {Excluded(cell)!.Value.Error}\n{rejected.Message}");
        }
    }

    /// <summary>Whether the form reads the target before it writes it: <c>??=</c> and a compound assignment.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Reads(Cell cell) => cell.Form is Form.CoalesceAssign or Form.CompoundBranching;

    private static bool HasIndex(Cell cell) =>
        cell.Target is Target.Indexer or Target.RefMethod or Target.RefIndexer or Target.ArrayCell or Target.SpanCell;

    /// <summary>The call that hands back the target's receiver.</summary>
    /// <param name="cell">The cell.</param>
    private static string ReceiverCall(Cell cell) => cell.Target switch
    {
        Target.ArrayCell => "GetArr",
        Target.SpanCell => "GetSpan",
        _ => "Get"
    };

    /// <summary>The call that hands back the target's reference, or null for a target that is no reference a member returns.</summary>
    /// <param name="cell">The cell.</param>
    private static string? ReferenceCall(Cell cell) => cell.Target switch
    {
        Target.RefMethod => "At",
        Target.RefProperty => "get_Slot",
        Target.RefIndexer => "RefCells.get_Item",
        _ => null
    };

    /// <summary>The getter and the setter of a target with accessor bodies, or null.</summary>
    /// <param name="cell">The cell.</param>
    private static (string Getter, string Setter)? Accessors(Cell cell) => cell.Target switch
    {
        Target.AccessorProperty => ("get_Q", "set_Q"),
        Target.Indexer => ("Holder.get_Item", "Holder.set_Item"),
        _ => null
    };

    /// <summary>The operations that read the target, which has no accessor bodies.</summary>
    /// <param name="body">The lowered member.</param>
    /// <param name="cell">The cell.</param>
    private static IrOperation[] Reads(Lowered body, Cell cell) => cell.Target switch
    {
        Target.RefMethod or Target.RefProperty or Target.RefIndexer => body.Operations.OfType<IrLoadReferenceOperation>().ToArray<IrOperation>(),
        Target.ArrayCell or Target.SpanCell => body.Operations.OfType<IrLoadElementOperation>().ToArray<IrOperation>(),
        _ => body.Operations.OfType<IrLoadFieldOperation>().Where(load => load.Field.Name == FieldName(cell)).ToArray<IrOperation>()
    };

    /// <summary>The operations that write the target, which has no accessor bodies.</summary>
    /// <param name="body">The lowered member.</param>
    /// <param name="cell">The cell.</param>
    private static IrOperation[] Stores(Lowered body, Cell cell) => cell.Target switch
    {
        Target.RefMethod or Target.RefProperty or Target.RefIndexer => body.Operations.OfType<IrStoreReferenceOperation>().ToArray<IrOperation>(),
        Target.ArrayCell or Target.SpanCell => body.Operations.OfType<IrStoreElementOperation>().ToArray<IrOperation>(),
        _ => body.Operations.OfType<IrStoreFieldOperation>().Where(store => store.Field.Name == FieldName(cell)).ToArray<IrOperation>()
    };

    private static string FieldName(Cell cell) => cell.Target switch
    {
        Target.Field => "F",
        Target.AutoProperty => "P",
        Target.StructFieldPath => cell.Value == Value.WholeStruct ? "Inner" : "X",
        _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
    };

    /// <summary>The value a store or a read of the target goes through, built from the receiver and the index the target was taken
    /// with: the receiver itself, the address of the struct field <c>S</c> on it, or the reference the reference-returning member hands
    /// back for them.</summary>
    /// <param name="body">The lowered member.</param>
    /// <param name="cell">The cell.</param>
    /// <param name="receiver">The value <see cref="ReceiverCall"/> gave.</param>
    /// <param name="index">The value <c>I</c> gave, or null.</param>
    private static int Place(Lowered body, Cell cell, int receiver, int? index)
    {
        switch (cell.Target)
        {
            case Target.StructFieldPath:
            {
                var address = Assert.Single(body.Operations.OfType<IrAddressFieldOperation>(), operation => operation.Field.Name == "S");
                Assert.True(address.ReceiverValue is int holder && body.Origin(holder) == receiver, $"{cell}: S is addressed on another receiver\n{body.Text}");
                return address.ResultValue;
            }
            case Target.RefMethod:
            {
                var call = Assert.Single(body.Calls("At"));
                Assert.True(call.ArgumentValues.Select(body.Origin).SequenceEqual([receiver, index!.Value]), $"{cell}: At is given other arguments\n{body.Text}");
                return call.ResultValue!.Value;
            }
            case Target.RefProperty:
            {
                var call = Assert.Single(body.Calls("get_Slot"));
                Assert.True(call.ReceiverValue is int holder && body.Origin(holder) == receiver, $"{cell}: Slot is read on another receiver\n{body.Text}");
                return call.ResultValue!.Value;
            }
            case Target.RefIndexer:
            {
                var call = Assert.Single(body.Calls("RefCells.get_Item"));
                var cells = Assert.Single(body.Operations.OfType<IrLoadFieldOperation>(), operation => operation.Field.Name == "Cells");
                Assert.True(cells.ReceiverValue is int holder && body.Origin(holder) == receiver, $"{cell}: Cells is read on another receiver\n{body.Text}");
                Assert.True(call.ReceiverValue is int named && body.Origin(named) == cells.ResultValue, $"{cell}: the indexer runs on another receiver\n{body.Text}");
                Assert.True(call.ArgumentValues.Select(body.Origin).SequenceEqual([index!.Value]), $"{cell}: the indexer is given another index\n{body.Text}");
                return call.ResultValue!.Value;
            }
            default:
                return receiver;
        }
    }

    /// <summary>An accessor's call is on the receiver and with the index the target was taken with.</summary>
    /// <param name="body">The lowered member.</param>
    /// <param name="cell">The cell.</param>
    /// <param name="called">The receiver the accessor is called on.</param>
    /// <param name="arguments">The arguments the accessor is given, the setter's value left out.</param>
    /// <param name="receiver">The value <see cref="ReceiverCall"/> gave.</param>
    /// <param name="index">The value <c>I</c> gave, or null.</param>
    private static void AssertPlace(Lowered body, Cell cell, int? called, IEnumerable<int> arguments, int receiver, int? index)
    {
        Assert.True(called is int value && body.Origin(value) == receiver, $"{cell}: an accessor runs on another receiver\n{body.Text}");
        Assert.True(arguments.Select(body.Origin).SequenceEqual(index is int named ? [named] : []), $"{cell}: an accessor is given another index\n{body.Text}");
    }

    private static Lowered Lower(Cell cell) =>
        Bodies.GetOrAdd(cell, key => new Lazy<Lowered>(() =>
        {
            var compilation = Compile(key);
            var type = compilation.GetTypeByMetadataName("C") ?? throw new InvalidOperationException("Type C was not found.");
            var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
            return new Lowered(IrLowering.Lower(method, compilation, ROOT_DIRECTORY, CancellationToken.None).Body);
        })).Value;

    private static Compilation Compile(Cell cell)
    {
        var solution = FixtureSolution.Create(("Case.cs", Source(cell)));
        return solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult() ?? throw new InvalidOperationException();
    }

    /// <summary>The member <c>M</c> making the cell's assignment, with every target's members over the type the value has.</summary>
    /// <param name="cell">The cell.</param>
    private static string Source(Cell cell)
    {
        var type = cell.Value == Value.WholeStruct ? "W" : cell.Target == Target.StructFieldPath ? "int?" : "R";
        var target = cell.Target switch
        {
            Target.Field => "Get().F",
            Target.AutoProperty => "Get().P",
            Target.AccessorProperty => "Get().Q",
            Target.Indexer => "Get()[I()]",
            Target.RefMethod => "At(Get(), I())",
            Target.RefProperty => "Get().Slot",
            Target.RefIndexer => "Get().Cells[I()]",
            Target.ArrayCell => "GetArr()[I()]",
            Target.SpanCell => "GetSpan()[I()]",
            Target.StructFieldPath => cell.Value == Value.WholeStruct ? "Get().S.Inner" : "Get().S.X",
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        var statement = cell.Form switch
        {
            Form.CoalesceAssign => $"{target} ??= V();",
            Form.Conditional => $"{target} = F() ? A() : B();",
            Form.Coalesce => $"{target} = A() ?? B();",
            Form.ObjectInitializer => $"{target} = new {type} {{ X = V() }};",
            Form.CompoundBranching => $"{target} += F() ? A() : B();",
            Form.DeconstructionTarget => $"{type} u; ({target}, u) = (A(), B());",
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        var made = cell.Form == Form.ObjectInitializer ? "int" : type;
        return $$"""
            using System;

            public class R { public int X; public static R operator +(R a, R b) => a; }
            public struct W { public int X; public static W operator +(W a, W b) => a; }
            public struct Pod { public int? X; public W Inner; }
            public class RefCells { public {{type}} Cell; public ref {{type}} this[int i] => ref Cell; }

            public class Holder
            {
                public {{type}} F;
                public {{type}} P { get; set; }
                public {{type}} Kept;
                public {{type}} Q { get { return Kept; } set { Kept = value; } }
                public {{type}} this[int i] { get { return Kept; } set { Kept = value; } }
                public ref {{type}} Slot => ref Kept;
                public RefCells Cells = new();
                public Pod S;
            }

            public class C
            {
                private readonly Holder _holder = new();
                private readonly {{type}}[] _cells = new {{type}}[2];
                Holder Get() => _holder;
                {{type}}[] GetArr() => _cells;
                Span<{{type}}> GetSpan() => _cells;
                static ref {{type}} At(Holder holder, int index) => ref holder.Kept;
                int I() => 0;
                bool F() => true;
                {{type}} A() => default!;
                {{type}} B() => default!;
                {{made}} V() => default!;

                void M()
                {
                    {{statement}}
                }
            }
            """;
    }

    /// <summary>A lowered member, its operations in the order of its blocks.</summary>
    private sealed class Lowered
    {
        private readonly Dictionary<IrOperation, int> _positions = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, int> _copies = [];

        public Lowered(IrBody body)
        {
            Operations = body.Blocks.OrderBy(block => block.Ordinal).SelectMany(block => block.Operations).ToArray();
            for (var position = 0; position < Operations.Length; position++)
                _positions[Operations[position]] = position;
            // A value one assignment defines is the value it copies.
            foreach (var group in Operations.OfType<IrAssignOperation>().GroupBy(assign => assign.TargetValue).Where(group => group.Count() == 1))
                _copies[group.Key] = group.Single().SourceValue;
            Text = IrPrinter.Print(body);
        }

        public IrOperation[] Operations { get; }

        public string Text { get; }

        public int Position(IrOperation operation) => _positions[operation];

        /// <summary>The calls of the members named, by the name the call's method ends its path with.</summary>
        /// <param name="names">Each a member name, or a type and a member name.</param>
        public IrCallOperation[] Calls(params string[] names) =>
            Operations.OfType<IrCallOperation>().Where(call => names.Any(name => Names(call.Method, name))).ToArray();

        /// <summary>The value a value was copied from, through every copy.</summary>
        /// <param name="value">The value.</param>
        public int Origin(int value)
        {
            while (_copies.TryGetValue(value, out var source))
                value = source;
            return value;
        }

        private static bool Names(string method, string name)
        {
            var at = method.IndexOf($"{name}(", StringComparison.Ordinal);
            return at >= 0 && (at == 0 || method[at - 1] is '.' or ' ' or ':');
        }
    }
}
