using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Heap;

/// <summary>The bounds of the heap analysis. <see cref="MaxAccessPathDepth"/> is the number of field segments a path keeps from
/// its base before it collapses to a wildcard.</summary>
public sealed record AnalysisLimits(int MaxAccessPathDepth = 8, int MaxContextsPerMethod = 16, int MaxSccIterations = 16)
{
    public static AnalysisLimits Default { get; } = new();
}

/// <summary>Where an object or a delegate is created: the body, the operation, the created type and the frontend's site ordinal.</summary>
public sealed record CreationSite(string BodyId, int OperationId, string TypeKey, int SiteOrdinal);

/// <summary>A value a summary is symbolic in; nothing in it names a context.</summary>
public abstract record AbstractValue;

public sealed record ThisValue : AbstractValue
{
    public static ThisValue Instance { get; } = new();

    public override string ToString() => "this";
}

public sealed record ParameterValue(int Ordinal) : AbstractValue
{
    public override string ToString() => $"param:{Ordinal}";
}

/// <summary>The object held in a static field.</summary>
public sealed record StaticFieldValue(IrFieldRef Field) : AbstractValue
{
    public override string ToString() => $"static:{Field.Assembly}:{Field.ContainingTypeId}.{Field.Name}";
}

public sealed record AllocationValue(CreationSite Site) : AbstractValue
{
    public override string ToString() => $"alloc:{Site.BodyId}#{Site.OperationId}";
}

public sealed record CallResultValue(int OperationId) : AbstractValue
{
    public override string ToString() => $"call:{OperationId}";
}

public sealed record RefResultValue(int OperationId, int Ordinal) : AbstractValue
{
    public override string ToString() => $"ref:{OperationId}:{Ordinal}";
}

/// <summary>A delegate created at <see cref="Site"/> for <see cref="Target"/> (a nested body id, a method id or a method symbol).
/// <see cref="CapturedValues"/> maps each captured symbol key, and <c>this</c>, to the union of the variable's values in the creating
/// body; two creation values at one site are equal whatever they captured.</summary>
public sealed record DelegateCreationValue(CreationSite Site, string Target, IReadOnlyDictionary<string, IReadOnlySet<AbstractValue>> CapturedValues)
    : AbstractValue
{
    public bool Equals(DelegateCreationValue? other) => other is not null && Site == other.Site && Target == other.Target;

    public override int GetHashCode() => HashCode.Combine(Site, Target);

    public override string ToString() => $"delegate:{Site.BodyId}#{Site.OperationId}";
}

/// <summary>A variable a nested body captures from the body that creates it: every version, on either side.</summary>
public sealed record CapturedValue(string SymbolKey) : AbstractValue
{
    public override string ToString() => $"captured:{SymbolKey}";
}

/// <summary>The storage slot of a field: its declaring type, assembly-qualified as a type key is, and its name, so a field a
/// derived type hides with <c>new</c>, and a same-named field of a same-named type of another assembly, are slots of their own
/// (R5). <c>[]</c> and <c>*</c> are slots without a declaring type. The declaring type drops its type arguments, since the same
/// slot is named <c>Base&lt;T&gt;.Value</c> inside the definition and <c>Base&lt;Order&gt;.Value</c> outside it, while the region
/// already says which closed object holds it.</summary>
public static class FieldSlot
{
    public static string Key(IrFieldRef field) => $"{TypeKey(field.Assembly, field.ContainingTypeId)}.{field.Name}";

    /// <summary>A declaring type in the form a slot key carries it.</summary>
    public static string TypeKey(string assembly, string type) => $"{assembly}:{WithoutTypeArguments(type)}";

    /// <summary>The declaring type key a slot key starts with, empty for <c>[]</c> and <c>*</c>.</summary>
    public static string DeclaringTypeKey(string key) => key.LastIndexOf('.') is var dot && dot >= 0 ? key[..dot] : "";

    /// <summary>The field name a slot key ends with, which is what the report and the expectations show.</summary>
    public static string Name(string key) => key.LastIndexOf('.') is var dot && dot >= 0 ? key[(dot + 1)..] : key;

    /// <summary>A type key without its type arguments, as a slot key carries it.</summary>
    public static string WithoutTypeArguments(string type)
    {
        var name = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var character in type)
        {
            if (character == '<')
                depth++;
            else if (character == '>')
                depth--;
            else if (depth == 0)
                name.Append(character);
        }

        return name.ToString();
    }
}

/// <summary>The object reached from <see cref="Base"/> through field segments; <c>[]</c> is an array element, and a path longer
/// than the depth limit is the single segment <c>*</c>.</summary>
public sealed record PathValue(AbstractValue Base, IReadOnlyList<string> Segments) : AbstractValue
{
    public const string WILDCARD = "*";
    public const string ELEMENT = "[]";

    public bool IsWildcard => Segments is [WILDCARD];

    public bool Equals(PathValue? other) => other is not null && Base.Equals(other.Base) && Segments.SequenceEqual(other.Segments);

    public override int GetHashCode() => HashCode.Combine(Base, string.Join('', Segments));

    public override string ToString() => $"({Base}).{string.Join('.', Segments)}";
}

/// <summary>What a stored or returned value was computed from.</summary>
public abstract record ValueDependency;

/// <summary>The value of the field load with this operation id.</summary>
public sealed record LoadDependency(int OperationId) : ValueDependency;

public sealed record ParameterDependency(int Ordinal) : ValueDependency;

public sealed record CapturedDependency(string SymbolKey) : ValueDependency;

/// <summary>The result of the call with this operation id; the callee decides what that depends on.</summary>
public sealed record CallDependency(int OperationId) : ValueDependency;

/// <summary>The new value of a <c>ref</c> or <c>out</c> argument of the call with this operation id; what the callee's parameter
/// of this ordinal holds when it ends decides what that depends on.</summary>
public sealed record RefResultDependency(int OperationId, int Ordinal) : ValueDependency;

/// <summary>A lock the body must hold at an operation: the values of its lock object, empty when they are unknown.</summary>
public sealed record HeldLockValue(IReadOnlySet<AbstractValue> Values, int AcquisitionId);

public enum SummaryAccessKind
{
    Load,
    Store
}

/// <summary>A field or auto-property load or store. <see cref="Bases"/> are the objects it touches, empty for a static field;
/// <see cref="Values"/> are the loaded or stored values, and a store's <see cref="Dependencies"/> are what its value depends on.</summary>
public sealed record SummaryAccess(int OperationId, SummaryAccessKind Kind, IrFieldRef Field, IReadOnlySet<AbstractValue> Bases,
                                   IrProvenance Provenance, IReadOnlyList<HeldLockValue> HeldLocks, int? ReadModifyWriteOf,
                                   IReadOnlySet<AbstractValue> Values, IReadOnlySet<ValueDependency> Dependencies);

/// <summary>A reference-typed field or static store: the field of each base (none for a static) now points to the values.</summary>
public sealed record StoreTransfer(int OperationId, IrFieldRef Field, IReadOnlySet<AbstractValue> Bases, IReadOnlySet<AbstractValue> Values);

public enum ElementOperationKind
{
    Load,
    Store
}

/// <summary>An array element load or store: points-to only, never an access.</summary>
public sealed record ElementTransfer(int OperationId, ElementOperationKind Kind, IReadOnlySet<AbstractValue> Arrays,
                                     IReadOnlySet<AbstractValue> Values);

public sealed record ReturnTransfer(int OperationId, IReadOnlySet<AbstractValue> Values, IReadOnlySet<ValueDependency> Dependencies);

/// <summary>The values a <c>ref</c> or <c>out</c> parameter may hold when the body ends, and what they were computed from.</summary>
public sealed record RefParameterTransfer(int Ordinal, IReadOnlySet<AbstractValue> Values)
{
    public IReadOnlySet<ValueDependency> Dependencies { get; init; } = new HashSet<ValueDependency>();
}

/// <summary>A delegate creation; the target type keys are the method group's containing type and method type arguments as the
/// creation names them, in the creating body's own type parameters.</summary>
public sealed record DelegateTransfer(int OperationId, DelegateCreationValue Delegate, IReadOnlySet<AbstractValue> Receivers,
                                      string? TargetContainingTypeKey = null, IReadOnlyList<string>? TargetMethodTypeArgumentKeys = null);

/// <summary>A call argument bound to its parameter, with what its value depends on.</summary>
public sealed record CallArgument(int ParameterOrdinal, IReadOnlySet<AbstractValue> Values)
{
    public IReadOnlySet<ValueDependency> Dependencies { get; init; } = new HashSet<ValueDependency>();
}

/// <summary>A monitor acquisition or release: the values of its lock object, and the IR value the object comes from through
/// assigns and conversions, which matches a lock statement's release to its acquisition when the values are unknown.</summary>
public sealed record LockTransfer(int OperationId, bool IsAcquire, IReadOnlySet<AbstractValue> Values, int Origin, IrProvenance Provenance);

/// <summary>A call into a body the scope may have: <see cref="Target"/> is the method id, or the nested body id of a local function.
/// The target type keys are as the call names them, in the calling body's own type parameters.</summary>
public sealed record CallTransfer(int OperationId, string Target, IrCallKind Kind, IReadOnlySet<AbstractValue> Receivers,
                                  IReadOnlyList<CallArgument> Arguments, IReadOnlyList<HeldLockValue> HeldLocks,
                                  string? TargetContainingTypeKey = null, IReadOnlyList<string>? TargetMethodTypeArgumentKeys = null);

/// <summary>A call into a method without a source body; it transfers nothing, and the delegates passed to it are not invoked.
/// <see cref="Receivers"/>, <see cref="Arguments"/> and <see cref="ServiceCall"/> let the DI semantics model registration, locator
/// and scope calls.</summary>
public sealed record SummaryOpaqueCall(int OperationId, string Callee, IReadOnlyList<DelegateCreationValue> Delegates)
{
    public IReadOnlySet<AbstractValue> Receivers { get; init; } = new HashSet<AbstractValue>();
    public IReadOnlyList<CallArgument> Arguments { get; init; } = [];
    public IrServiceCall? ServiceCall { get; init; }
}

/// <summary>An assignment, in a nested body, to a variable it captures: task 5 joins it with the outer variable.</summary>
public sealed record CapturedStore(int OperationId, string SymbolKey, IReadOnlySet<AbstractValue> Values,
                                   IReadOnlySet<ValueDependency> Dependencies);

/// <summary>The union of every version of a local or parameter in the body.</summary>
public sealed record SummaryVariable(string SymbolKey, IReadOnlySet<AbstractValue> Values, IReadOnlySet<ValueDependency> Dependencies);

public sealed record MethodSummary(string BodyId, IReadOnlyList<SummaryAccess> Accesses, IReadOnlyList<StoreTransfer> Stores,
                                   IReadOnlyList<ElementTransfer> Elements, IReadOnlyList<ReturnTransfer> Returns,
                                   IReadOnlyList<RefParameterTransfer> RefParameters, IReadOnlyList<DelegateTransfer> Delegates,
                                   IReadOnlyList<CallTransfer> Calls, IReadOnlyList<SummaryOpaqueCall> OpaqueCalls,
                                   IReadOnlyList<CapturedStore> CapturedStores, IReadOnlyList<SummaryVariable> Variables)
{
    public IReadOnlyList<LockTransfer> Locks { get; init; } = [];
}
