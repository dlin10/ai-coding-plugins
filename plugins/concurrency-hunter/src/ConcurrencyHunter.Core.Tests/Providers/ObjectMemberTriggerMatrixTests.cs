using System.Collections.Concurrent;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>A member <c>System.Object</c> itself implements for the holder's type — <c>ToString</c>, <c>Equals(object)</c>,
/// <c>GetHashCode</c>, overridden neither by the type nor by its bases — is not a trigger when the holder's type is exact: sealed, or
/// the type a constructor made, whoever declares it: object, or an interface of the type that object's member implements. An
/// override, <c>System.ValueType</c>'s and a record's included, stays a trigger, and so does an interface's member the type implements
/// explicitly.</summary>
public sealed class ObjectMemberTriggerMatrixTests
{
    private enum Holder { ConstructorResult, MethodResult, Receiver }
    private enum Type { Sealed, Unsealed }
    private enum Implementer { Object, HolderType, LibraryBase, ValueType, RecordClass, RecordStruct, ExplicitInterface }
    private enum Member { ToString, Equals, GetHashCode }

    /// <summary>Who declares object's three members for the holder's type: object alone, or also an interface <c>IO</c> the type
    /// implements.</summary>
    private enum Declarer { Object, Interface }

    private sealed record Cell(Holder Holder, Type Type, Implementer Implementer, Declarer Declarer, Member Member);

    private static readonly Cell[] Cells = (from holder in Enum.GetValues<Holder>()
                                            from type in Enum.GetValues<Type>()
                                            from implementer in Enum.GetValues<Implementer>()
                                            from declarer in Enum.GetValues<Declarer>()
                                            from member in Enum.GetValues<Member>()
                                            where Allowed(holder, type, implementer, declarer)
                                            select new Cell(holder, type, implementer, declarer, member)).ToArray();

    [Fact]
    public void Every_cell_gets_its_expected_trigger_set()
    {
        var wrong = new ConcurrentQueue<string>();
        Parallel.ForEach(Cells, CellParallelism, cell =>
        {
            try { Check(cell); }
            catch (Exception error) { wrong.Enqueue($"{cell}: {error.Message}"); }
        });
        Assert.True(wrong.IsEmpty, $"{wrong.Count} of {Cells.Length} cells:\n" + string.Join('\n', wrong.Take(20)));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        Assert.Equal(Cells.Length, Cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<Holder>(), Cells.Select(cell => cell.Holder).Distinct().Order());
        Assert.Equal(Enum.GetValues<Type>(), Cells.Select(cell => cell.Type).Distinct().Order());
        Assert.Equal(Enum.GetValues<Implementer>(), Cells.Select(cell => cell.Implementer).Distinct().Order());
        Assert.Equal(Enum.GetValues<Declarer>(), Cells.Select(cell => cell.Declarer).Distinct().Order());
        Assert.Equal(Enum.GetValues<Member>(), Cells.Select(cell => cell.Member).Distinct().Order());
    }

    [Fact]
    public void A_message_parser_shaped_holder_fits_the_24_triggers()
    {
        // A sealed generic parser whose 11 members hide 11 of its unsealed base's, made by its constructor, overriding nothing of
        // object's: 22 triggers, none of them object's, and nothing past the 24.
        var own = string.Concat(Enumerable.Range(0, 11).Select(index => $"public new T P{index:00}(byte[] data) => _factory(); "));
        var hidden = string.Concat(Enumerable.Range(0, 11).Select(index => $"public object P{index:00}(byte[] data) => null; "));
        var driver = Synthesize($$"""
            public class Parser { internal Parser() { } {{hidden}} }
            public sealed class Parser<T> : Parser { private readonly Func<T> _factory; public Parser(Func<T> factory) { _factory = factory; } {{own}} }
            """, "M:Lib.Parser`1.#ctor(System.Func{`0})");

        Assert.Equal(22, driver.Triggers.Count);
        Assert.All(driver.Triggers, trigger => Assert.Equal(FateClassifier.RESULT, trigger.Holder));
        Assert.Equal(11, driver.Triggers.Count(trigger => trigger.Member.StartsWith("M:Lib.Parser`1.P", StringComparison.Ordinal)));
        Assert.Equal(11, driver.Triggers.Count(trigger => trigger.Member.StartsWith("M:Lib.Parser.P", StringComparison.Ordinal)));
        Assert.DoesNotContain(driver.Triggers, trigger => trigger.Member.StartsWith("M:System.Object.", StringComparison.Ordinal));
        Assert.DoesNotContain(driver.Uncovered[FateClassifier.RESULT], why => why.Contains("past the driver's 24 triggers", StringComparison.Ordinal));
        Assert.True(driver.Covers(FateClassifier.RESULT), string.Join("; ", driver.Uncovered[FateClassifier.RESULT]));
    }

    // A struct and a record struct cannot be unsealed; a struct a method returns is no holder the driver builds (only a constructor's
    // result or one of a reference type is); a class cannot derive from a record class, so the driver's subclass of an unsealed one —
    // the receiver, and the argument of the record's own Equals(H) — does not compile, whatever the holder; an explicit
    // implementation is of an interface the type implements.
    private static bool Allowed(Holder holder, Type type, Implementer implementer, Declarer declarer) =>
        !(type == Type.Unsealed && implementer is Implementer.ValueType or Implementer.RecordStruct or Implementer.RecordClass) &&
        !(holder == Holder.MethodResult && implementer is Implementer.ValueType or Implementer.RecordStruct) &&
        !(implementer == Implementer.ExplicitInterface && declarer == Declarer.Object);

    private static void Check(Cell cell)
    {
        var memberId = cell.Holder switch
        {
            Holder.ConstructorResult => "M:Lib.H.#ctor(System.Action)",
            Holder.MethodResult => "M:Lib.Api.Make(System.Action)",
            _ => "M:Lib.H.On(System.Action)"
        };
        var driver = Synthesize(Types(cell), memberId);
        var kind = cell.Holder == Holder.Receiver ? FateClassifier.THIS : FateClassifier.RESULT;

        // object's three members as the holder's triggers, by the type implementing each.
        var exact = cell.Type == Type.Sealed || cell.Holder == Holder.ConstructorResult;
        var expected = Enum.GetValues<Member>().SelectMany(member => Expected(cell, member, exact)).Order(StringComparer.Ordinal);
        var actual = driver.Triggers.Where(trigger => trigger.Holder == kind && IsObjectMember(trigger.Member))
                                    .Select(trigger => trigger.Member)
                                    .Order(StringComparer.Ordinal);
        Assert.Equal(expected, actual);

        // A member that is not a trigger is not uncovered either: the holder has nothing to observe through it.
        Assert.DoesNotContain(driver.Uncovered[kind], why => Enum.GetValues<Member>().Any(member => why.Contains(Signature(member) + ": ", StringComparison.Ordinal)));
        Assert.Single(driver.Triggers, trigger => trigger.Holder == kind && trigger.Member == "M:Lib.H.On(System.Action)");
    }

    /// <summary>The triggers one of object's members gives the holder (R3): an override, by the type declaring it, which stands for the
    /// interface's declaration too; object's own implementation, by object and by the interface, only when the holder is not exact;
    /// the interface's member the type implements explicitly, always, with object's own beside it when the holder is not exact.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="member">The member.</param>
    /// <param name="exact">Whether the holder's type is exact.</param>
    private static IEnumerable<string> Expected(Cell cell, Member member, bool exact) => ImplementerOf(cell, member) switch
    {
        Implementer.ExplicitInterface => exact ? [Id(Implementer.ExplicitInterface, member)]
                                               : [Id(Implementer.ExplicitInterface, member), Id(Implementer.Object, member)],
        Implementer.Object when exact => [],
        Implementer.Object => cell.Declarer == Declarer.Interface
            ? [Id(Implementer.Object, member), Id(Implementer.ExplicitInterface, member)]
            : [Id(Implementer.Object, member)],
        var implementer => [Id(implementer, member)]
    };

    /// <summary>The type whose implementation a call of one of object's members on the holder reaches.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="member">The member.</param>
    private static Implementer ImplementerOf(Cell cell, Member member) =>
        cell.Implementer switch
        {
            Implementer.ValueType or Implementer.RecordClass or Implementer.RecordStruct => cell.Implementer,
            _ when member == cell.Member => cell.Implementer,
            _ => Implementer.Object
        };

    /// <summary>The trigger's member id of one of object's members as the implementing type declares it; for an explicit
    /// implementation, the interface's member it implements.</summary>
    /// <param name="implementer">The implementing type.</param>
    /// <param name="member">The member.</param>
    private static string Id(Implementer implementer, Member member)
    {
        var type = implementer switch
        {
            Implementer.Object => "System.Object",
            Implementer.LibraryBase => "Lib.Base",
            Implementer.ValueType => "System.ValueType",
            Implementer.ExplicitInterface => "Lib.IO",
            _ => "Lib.H"
        };
        return $"M:{type}.{Signature(member)}";
    }

    /// <summary>One of object's members as its id names it after the declaring type.</summary>
    /// <param name="member">The member.</param>
    private static string Signature(Member member) =>
        member switch { Member.ToString => "ToString", Member.Equals => "Equals(System.Object)", _ => "GetHashCode" };

    private static bool IsObjectMember(string id) => Enum.GetValues<Member>().Any(member => id.EndsWith("." + Signature(member), StringComparison.Ordinal));

    /// <summary>A cell's library: the holder type <c>H</c>, holding a delegate its constructor takes, with an <c>On</c> that takes
    /// one too; the override of the cell's member in <c>H</c> or in its base, or its explicit implementation of <c>IO</c>'s;
    /// the interface <c>IO</c> declaring object's three members, which <c>H</c> implements when the cell's declarer is the interface;
    /// and <c>Api.Make</c>, which returns a new <c>H</c>.</summary>
    /// <param name="cell">The cell.</param>
    private static string Types(Cell cell)
    {
        var sealing = cell.Type == Type.Sealed ? "sealed " : "";
        var @override = cell.Member switch
        {
            Member.ToString => "public override string ToString() => \"\";",
            Member.Equals => "public override bool Equals(object o) => false;",
            _ => "public override int GetHashCode() => 0;"
        };
        var @explicit = cell.Member switch
        {
            Member.ToString => "string IO.ToString() => \"\";",
            Member.Equals => "bool IO.Equals(object o) => false;",
            _ => "int IO.GetHashCode() => 0;"
        };
        var body = "private Action _a; public H(Action a) { _a = a; } public void On(Action a) { _a = a; }";
        var io = cell.Declarer == Declarer.Interface ? " : IO" : "";
        var holder = cell.Implementer switch
        {
            Implementer.HolderType => $"public {sealing}class H{io} {{ {body} {@override} }}",
            Implementer.LibraryBase => $"public class Base {{ {@override} }} public {sealing}class H : Base{(io.Length == 0 ? "" : ", IO")} {{ {body} }}",
            Implementer.ValueType => $"public struct H{io} {{ {body} }}",
            Implementer.RecordClass => $"public {sealing}record class H{io} {{ {body} }}",
            Implementer.RecordStruct => $"public record struct H{io} {{ {body} }}",
            Implementer.ExplicitInterface => $"public {sealing}class H{io} {{ {body} {@explicit} }}",
            _ => $"public {sealing}class H{io} {{ {body} }}"
        };
        return "public interface IO { string ToString(); bool Equals(object o); int GetHashCode(); } " + holder +
               " public static class Api { public static H Make(Action a) => new H(a); }";
    }

    private static Driver Synthesize(string types, string memberId)
    {
        var library = Compile("using System;\n\nnamespace Lib\n{\n" + types + "\n}\n");
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        var member = Assert.IsAssignableFrom<ISymbol>(DriverSynthesizer.FindMember(library.Compilation, memberId));
        var synthesis = DriverSynthesizer.Synthesize(library.Compilation, member, library.ExternMembers, library.OpenedFields, CancellationToken.None);
        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        Assert.Empty(synthesis.Driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return synthesis.Driver;
    }
}
