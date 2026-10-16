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
/// <para>The places that decide, layer by layer. The lowering (<c>IrLowering</c>) classifies a Roslyn operand only through
/// <c>StructOperandOf</c>, and a type is held as a struct where <c>HeldStructTypeKey</c> names it — a struct the analysis follows, or a
/// type parameter constrained to <c>struct</c>, whose key the heap substitutes per instance. <c>LowerStructOperand</c> asks
/// <see cref="IsPlace"/> for the receiver of a field, event or property access and of a member call, for the operand a boxing
/// conversion boxes and for the receiver a method-group delegate boxes (<see cref="StructUse.Copy"/>); <c>LowerArgument</c> asks it for
/// a <c>ref</c>, <c>out</c> or <c>in</c> argument; <c>LowerAddress</c> takes the address of what such an operand names, the
/// <c>Current</c> a <c>ref foreach</c> binds included (<c>IsReferenceCurrent</c>, a <see cref="StructOperand.ReferenceReturn"/>);
/// <c>LowerCapture</c> keeps whether a conditional reference is read-only as <c>StructOperandOf</c> says. A deconstruction whose
/// <c>Deconstruct</c> is the source's own instance member (<c>CollectDeconstruction</c>, then <c>CallDeconstruct</c>) runs it as any member
/// call does, its source lowered by <c>LowerStructOperand</c> with <see cref="StructUse.MemberCall"/>: on struct storage in place, on a
/// defensive copy where C# makes one — readonly storage, an <c>in</c> or <c>ref readonly</c> reference, a <c>ReadOnlySpan</c> cell, the
/// member not <c>readonly</c> —, and on a copy only where the source is one; an extension <c>Deconstruct</c>, a tuple and a pair take the
/// source by value. A pattern's designation (<c>Designated</c>) boxes a struct or a <c>Nullable</c> it designates as a reference type, as
/// the boxing conversion does, unboxes a copy out of a box, and takes any other input as it is.
/// The summary (<c>MethodSummaryBuilder</c>) classifies an IR value only through its <c>StructOperandOf</c>: <c>IsStructCopy</c>
/// asks <see cref="IsStorage"/> and <c>CopySources</c> fills each copy it makes; <c>WorksOnStorage</c> asks
/// <see cref="GoesThroughStorage"/> for the use an operation makes, which <c>CellAccesses</c>, <c>ReceiverCopies</c> and
/// <c>ReadsCell</c> follow; <c>EvaluatePoints</c> asks <see cref="IsStorage"/> before <c>StoragePlaces</c> names the storage a reference
/// to a field, a cell or a <c>ref foreach</c>'s <c>Current</c> names; <c>References</c>, <c>IsCopyReference</c> and
/// <c>StorageCell</c> follow those values. The heap (<c>WholeProgram.Eval</c>) makes a <c>StructPlaceValue</c> a storage region
/// (<c>Storage</c>) and a <c>StructCopyValue</c> a copy region (<c>CopyRegion</c>), and <c>CopyStruct</c>, <c>HoldsStruct</c> and
/// <c>HeapSolution.Located</c> decide by those regions alone. The accesses (<c>InterproceduralAccesses.Place</c>,
/// <c>IsUnresolvedBase</c>) and the generator (<c>EffectReader.StoreTargets</c>, <c>ReferenceStructStores</c>,
/// <c>ValueProvenance</c>) go through <c>Located</c>; <c>EffectReader.StructStorageStore</c> asks <see cref="IsStorage"/> of the
/// operand a struct input is. Two places read a type because no summary stands behind what they decide: <c>DeepReadAccesses</c>
/// follows the fields a field's declared struct type gives in storage unseen code reaches, and the callers of
/// <c>IsUnresolvedBase</c> tell a value type's operand apart. What a driver may seed in place (<c>SeedableFields</c>,
/// <c>DriverSynthesizer.SeedStruct</c>, <c>SeedMember</c>) is another question.</para>
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
        GoesThroughStorage(use) && IsStorage(operand) &&
        !(use == StructUse.MemberCall && isReadOnlyStorage && !isReadOnlyMember);

    /// <summary>Whether a use goes through the storage of the struct it names rather than taking its value: a field access, a member
    /// call and a reference argument do, a copy does not.</summary>
    /// <param name="use">How an operation uses a struct operand.</param>
    public static bool GoesThroughStorage(StructUse use) => use != StructUse.Copy;

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
