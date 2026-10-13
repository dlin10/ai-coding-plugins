namespace ConcurrencyHunter.Ir;

/// <summary>What names a struct an operation uses (<see cref="StructStorage"/>).</summary>
public enum StructOperand
{
    /// <summary>An instance field of an object, or a field of a struct's storage.</summary>
    Field,

    /// <summary>A static field.</summary>
    StaticField,

    /// <summary>A cell of an array, or of a span cut from one.</summary>
    Element,

    /// <summary>A variable a lambda or a local function captures, read where it is captured.</summary>
    Capture,

    /// <summary>A <c>ref</c> local.</summary>
    ReferenceLocal,

    /// <summary>A <c>ref</c> or <c>out</c> parameter.</summary>
    ReferenceParameter,

    /// <summary>An <c>in</c> or <c>ref readonly</c> parameter, or a <c>ref readonly</c> local.</summary>
    ReadOnlyReference,

    /// <summary>What a call, a property or an indexer returns by reference; read-only where it returns by <c>ref readonly</c>.</summary>
    ReferenceReturn,

    /// <summary><c>this</c> in a member of the struct: the storage the member was called on.</summary>
    Receiver,

    /// <summary>A local that is not captured.</summary>
    Local,

    /// <summary>A by-value parameter.</summary>
    Parameter,

    /// <summary>What a call, a conversion or a load hands back by value.</summary>
    Result,

    /// <summary>The value <c>new S()</c> or <c>default</c> makes.</summary>
    Creation
}

/// <summary>How an operation uses a struct operand (<see cref="StructStorage"/>).</summary>
public enum StructUse
{
    /// <summary>A field of the struct is read or written.</summary>
    FieldAccess,

    /// <summary>A member of the struct — a method, a property or indexer accessor, an event accessor — is called on it.</summary>
    MemberCall,

    /// <summary>It is passed as a <c>ref</c>, <c>out</c> or <c>in</c> argument.</summary>
    ReferenceArgument,

    /// <summary>Its value is taken: assigned to a local, passed by value, returned, stored, boxed or bound to a method-group
    /// delegate.</summary>
    Copy
}

/// <summary>
/// The one decision whether a struct operand is a place — the storage the struct occupies in its holder, which an access through it
/// reaches (R1) — or a copy, which is no location (R2). A struct of the program is followed field by field, as its holder's storage;
/// a struct held by value is a copy of its own.
/// <para>The lowering asks it for every operand kind of <see cref="StructOperand"/> where the operand is the receiver of a field
/// access (<see cref="StructUse.FieldAccess"/>), the receiver of a member call — a method, a property or indexer accessor, an event
/// accessor (<see cref="StructUse.MemberCall"/>) — or a <c>ref</c>, <c>out</c> or <c>in</c> argument
/// (<see cref="StructUse.ReferenceArgument"/>): a place is lowered as an address and a copy as a value. A by-value argument, a return,
/// an assignment, a store of a whole struct, a boxing and a method-group delegate made from a struct's member take a copy
/// (<see cref="StructUse.Copy"/>).</para>
/// <para>The summary builder asks <see cref="IsStorage"/> of every struct-typed value: a reference, a captured variable and the
/// receiver of a struct member name storage and alias it, every other value is a copy of its own. A copy is made at each transfer
/// that takes a value into a local or a parameter — an assignment or a phi, a conversion, a field, element or reference load, a
/// by-value parameter, an unboxing, a new value or a default; a call's or an await's result is the copy the callee or the task hands
/// back, and a temporary an assignment, a phi or a conversion defines is the value it was given — and moved into storage at each
/// transfer that puts one there — a field, element or reference store, a boxing. Each such transfer
/// moves what every reference field of the struct holds, and nothing else: no transfer ever puts the copy itself into a slot.</para>
/// </summary>
public static class StructStorage
{
    /// <summary>Whether an operand names storage a struct occupies — a field, a static field, a cell, a captured variable, a
    /// reference or the receiver of a struct member — rather than a copy held by value.</summary>
    /// <param name="operand">What names the struct.</param>
    public static bool IsStorage(StructOperand operand) =>
        operand is not (StructOperand.Local or StructOperand.Parameter or StructOperand.Result or StructOperand.Creation);

    /// <summary>Whether an operation reaches the storage of a struct operand, rather than a copy of it. Taking its value copies; so
    /// does calling a member that is not <c>readonly</c> on storage that is read-only — a <c>readonly</c> field outside its
    /// construction, an <c>in</c> parameter, a <c>ref readonly</c> reference or a cell of a <c>ReadOnlySpan</c> — which C# makes on a
    /// copy so that the storage stays as it is (R2). A <c>readonly</c> member makes no such copy.</summary>
    /// <param name="operand">What names the struct.</param>
    /// <param name="use">How the operation uses it.</param>
    /// <param name="isReadOnlyStorage">Whether the storage the operand names is read-only where the operation stands.</param>
    /// <param name="isReadOnlyMember">Whether a member called on it is <c>readonly</c>; ignored for any other use.</param>
    public static bool IsPlace(StructOperand operand, StructUse use, bool isReadOnlyStorage, bool isReadOnlyMember) =>
        use != StructUse.Copy && IsStorage(operand) &&
        !(use == StructUse.MemberCall && isReadOnlyStorage && !isReadOnlyMember);

    /// <summary>The whole storage of a box holding a struct of <paramref name="typeKey"/>: what an access names when it takes the struct
    /// out of the box as one, whose access path is empty (R3). It is no field of the struct; its name is empty.</summary>
    /// <param name="typeKey">The struct's type key.</param>
    public static IrFieldRef WholeStorage(string typeKey)
    {
        var colon = typeKey.IndexOf(':');
        var (assembly, type) = colon < 0 ? ("", typeKey) : (typeKey[..colon], typeKey[(colon + 1)..]);
        return new IrFieldRef(assembly, type, "", IrFieldKind.Field, false, false, type) { FieldTypeKey = typeKey };
    }

    /// <summary>Whether a field an access names is a box's whole storage (<see cref="WholeStorage"/>).</summary>
    /// <param name="field">The field.</param>
    public static bool IsWholeStorage(IrFieldRef field) => field.Name.Length == 0;
}
