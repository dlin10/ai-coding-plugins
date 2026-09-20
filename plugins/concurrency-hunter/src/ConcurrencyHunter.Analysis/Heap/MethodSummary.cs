using ConcurrencyHunter.Analysis;
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

/// <summary>The result of an await whose result is itself a task: the tail of the awaited task.</summary>
public sealed record AwaitResultValue(int OperationId) : AbstractValue
{
    public override string ToString() => $"await:{OperationId}";
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

/// <summary>A predicate of the path an access runs on, as one body states it (TD-090). <see cref="SubjectLoad"/> is the field
/// load it constrains, whose canonical identity the caller's context decides; <see cref="SubjectValue"/> names a value of this
/// body instead, which no other execution shares. A predicate with neither names nothing and only carries its
/// <see cref="Text"/> into the uncertainties (TD-095).</summary>
public sealed record SummaryPredicate(int? SubjectLoad, int? SubjectValue, PathRelation Relation, string? Value, string Text)
{
    /// <summary>The width in bits of the subject's own type, and whether that type is signed, carried so that the solver decides
    /// the guard in the width the language runs it in (TD-094).</summary>
    public int Width { get; init; } = 32;

    public bool Signed { get; init; } = true;
}

/// <summary>A field or auto-property load or store. <see cref="Bases"/> are the objects it touches, empty for a static field;
/// <see cref="Values"/> are the loaded or stored values, and a store's <see cref="Dependencies"/> are what its value depends on.</summary>
public sealed record SummaryAccess(int OperationId, SummaryAccessKind Kind, IrFieldRef Field, IReadOnlySet<AbstractValue> Bases,
                                   IrProvenance Provenance, IReadOnlyList<HeldLockValue> HeldLocks, int? ReadModifyWriteOf,
                                   IReadOnlySet<AbstractValue> Values, IReadOnlySet<ValueDependency> Dependencies)
{
    /// <summary>What the atomic mark of this load or store says it does to the cell, null when it carries none (TD-082).</summary>
    public IrAtomicEffect? Atomic { get; init; }

    /// <summary>
    /// The load whose value a compare-and-swap checks the cell against — the operation whose result the comparand <em>is</em>,
    /// not one it was computed from. Null for every other access, and for a comparand that is anything but a load's own value:
    /// <c>old + 2</c> depends on the read of <c>old</c> and is not that read's value, so a swap checking it verifies nothing
    /// about what the sequence observed and writes over whatever happened in between (R1).
    /// </summary>
    public int? ComparandLoad { get; init; }

    /// <summary>Which cell of the collection the access touches, null when it touches the field itself (TD-043).</summary>
    public ElementSelector? Selector { get; init; }

    /// <summary>Whether this access is the change of a check-then-act sequence over one collection, which is one compound
    /// operation however atomic each of its steps is (ADR 0010). Its <see cref="Dependencies"/> name the checks.</summary>
    public bool IsCompound { get; init; }

    /// <summary>The predicates that must hold for this access to run (TD-090).</summary>
    public IReadOnlyList<SummaryPredicate> Conditions { get; init; } = [];

    /// <summary>The ordinal of the parameter this access's cell is named by, when it is named by one: a parallel loop binds
    /// that parameter to the iteration number, which two iterations of one run never share (TD-068).</summary>
    public int? SelectorParameter { get; init; }

    /// <summary>The narrowest width in bits the cell's index passes through on its way from <see cref="SelectorParameter"/>,
    /// null where it passes through no conversion at all. An index converted narrower than the iteration numbers it carries
    /// repeats, and two iterations of one loop then name one cell (TD-068).</summary>
    public int? SelectorWidth { get; init; }

    /// <summary>The expression naming this access's cell, in the width of its own type, for the solver (TD-092).</summary>
    public ValueTerm? SelectorTerm { get; init; }

    /// <summary>Whether this access works on the collection a field holds, or on one of its cells, rather than on the field
    /// itself (ADR 0010). Such a resource is the collection object and not the field that reached it: two fields holding one
    /// dictionary hold one dictionary, and naming the resource after the field leaves their accesses unable to ever meet.</summary>
    public bool IsOnCollection { get; init; }
}

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

public sealed record ReturnTransfer(int OperationId, IReadOnlySet<AbstractValue> Values, IReadOnlySet<ValueDependency> Dependencies)
{
    /// <summary>Where the returned value may come from besides <see cref="Values"/>, as <see cref="SummaryValue"/> records it: a caller
    /// that sees only the regions cannot tell a returned object the heap named from one it could not.</summary>
    public IReadOnlySet<UnknownSource> UnknownSources { get; init; } = new HashSet<UnknownSource>();
    public IReadOnlySet<int> SourceCalls { get; init; } = new HashSet<int>();
}

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
public sealed record LockTransfer(int OperationId, bool IsAcquire, IrSynchronizationPrimitive Primitive, IrLockMode Mode,
                                  IReadOnlySet<AbstractValue> Values, int Origin, IrProvenance Provenance)
{
    /// <summary>How many permits an exit gives back, as <see cref="IrReleaseOperation.Permits"/> reads it; an entry takes one.</summary>
    public int? Permits { get; init; } = 1;
}

/// <summary>A call into a body the scope may have: <see cref="Target"/> is the method id, or the nested body id of a local function.
/// The target type keys are as the call names them, in the calling body's own type parameters.</summary>
public sealed record CallTransfer(int OperationId, string Target, IrCallKind Kind, IReadOnlySet<AbstractValue> Receivers,
                                  IReadOnlyList<CallArgument> Arguments, IReadOnlyList<HeldLockValue> HeldLocks,
                                  string? TargetContainingTypeKey = null, IReadOnlyList<string>? TargetMethodTypeArgumentKeys = null)
{
    public bool IsAwaitedImmediately { get; init; }

    /// <summary>Where the receiver may come from besides <see cref="Receivers"/>, as <see cref="SummaryValue"/> records it: a dispatch
    /// over the regions alone cannot tell a receiver the heap named from one it could not.</summary>
    public IReadOnlySet<UnknownSource> ReceiverUnknownSources { get; init; } = new HashSet<UnknownSource>();
    public IReadOnlySet<int> ReceiverSourceCalls { get; init; } = new HashSet<int>();

    /// <summary>The predicates that must hold for the call to run, in the calling body's own values. Everything the callee does
    /// runs under them too, so the guards of a call site are part of the condition of every access it reaches (R8, TD-090).</summary>
    public IReadOnlyList<SummaryPredicate> Conditions { get; init; } = [];
}

/// <summary>A call into a method without a source body; it transfers nothing, and the delegates passed to it are not invoked. The
/// work and callbacks of a recognized spawn or timer are not among <see cref="Delegates"/>: the spawn runs them.
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
public sealed record SummaryVariable(string SymbolKey, IReadOnlySet<AbstractValue> Values, IReadOnlySet<ValueDependency> Dependencies)
{
    public IReadOnlySet<UnknownSource> UnknownSources { get; init; } = new HashSet<UnknownSource>();
}

/// <summary>Where a value may come from that the analysis does not follow to an object: <c>null</c> or a default, a parameter,
/// a field before its first write, an opaque call, a captured variable or anything else it does not model. A
/// <see cref="SourceCall"/> result is what the callee returns; the heap knows it only for an async spawn, whose result is its
/// handle (<c>HeapSolution.AsyncSpawns</c>).</summary>
public enum UnknownSource
{
    Null,
    Parameter,
    FieldBeforeWrite,
    OpaqueCall,
    SourceCall,
    Captured,
    Other
}

/// <summary>A value an event names, with the unknown sources it may also come from; an empty set means it comes only from what
/// <see cref="Values"/> name.</summary>
public sealed record SummaryValue(IReadOnlySet<AbstractValue> Values, IReadOnlySet<UnknownSource> UnknownSources)
{
    /// <summary>The calls whose results the value comes from as <see cref="UnknownSource.SourceCall"/>: what each of them returns is the
    /// callee's business, so only the region a call is known to produce excuses it.</summary>
    public IReadOnlySet<int> SourceCalls { get; init; } = new HashSet<int>();
}

/// <summary>Work a BCL call starts (<see cref="IrSpawnOperation"/>): <see cref="Handle"/> is the call's result, or the started
/// thread for <see cref="IrSpawnKind.ThreadStart"/>, whose work a <see cref="SummaryThreadWork"/> binds.</summary>
public sealed record SummarySpawn(int OperationId, int CallOperationId, IrSpawnKind Kind, SummaryValue? Handle,
                                  IReadOnlyList<SummaryValue> Work, IrProvenance Provenance)
{
    public SummaryValue? State { get; init; }
    public SummaryValue? Antecedent { get; init; }
    public string? WorkMethod { get; init; }
    public bool WorkIsAsync { get; init; }
    public bool AwaitsWorkTask { get; init; }
    public bool JoinsOnReturn { get; init; }
}

public sealed record SummaryThreadWork(int OperationId, SummaryValue Thread, SummaryValue Work, bool WorkIsAsync, IrProvenance Provenance);

public enum SummaryJoinKind
{
    Await,
    Wait,
    Join,
    WaitAll,
    WaitOne
}

/// <summary>A wait for handles: an await, which throws only after its task completes, or a joining BCL call, which may throw
/// earlier. <see cref="CallOperationId"/> is null for an await.</summary>
public sealed record SummaryJoin(int OperationId, SummaryJoinKind Kind, int? CallOperationId, IReadOnlyList<SummaryValue> Handles,
                                 bool HandlesKnown, bool ThrowsOnlyAfterCompletion, IrProvenance Provenance);

/// <summary>A <c>Task.WhenAll</c> call; its result is a task that completes after <see cref="Tasks"/>, unless they are unknown.</summary>
public sealed record SummaryWhenAll(int OperationId, int CallOperationId, IReadOnlyList<SummaryValue> Tasks, bool TasksKnown,
                                    IrProvenance Provenance);

/// <summary>An <c>Unwrap()</c> call: its result is the tail of <see cref="Outer"/>.</summary>
public sealed record SummaryUnwrap(int OperationId, int CallOperationId, SummaryValue Outer, IrProvenance Provenance);

/// <summary>A timer step (<see cref="IrTimerOperation"/>) with its values.</summary>
public sealed record SummaryTimer(int OperationId, IrTimerAction Action, SummaryValue Timer, IrProvenance Provenance)
{
    public SummaryValue? Callback { get; init; }
    public SummaryValue? State { get; init; }
    public IrTimerInterval? DueTime { get; init; }
    public IrTimerInterval? Period { get; init; }
    public SummaryValue? WaitHandle { get; init; }
    public SummaryValue? Result { get; init; }
    public IrTimerFlag? Flag { get; init; }
}

public sealed record MethodSummary(string BodyId, IReadOnlyList<SummaryAccess> Accesses, IReadOnlyList<StoreTransfer> Stores,
                                   IReadOnlyList<ElementTransfer> Elements, IReadOnlyList<ReturnTransfer> Returns,
                                   IReadOnlyList<RefParameterTransfer> RefParameters, IReadOnlyList<DelegateTransfer> Delegates,
                                   IReadOnlyList<CallTransfer> Calls, IReadOnlyList<SummaryOpaqueCall> OpaqueCalls,
                                   IReadOnlyList<CapturedStore> CapturedStores, IReadOnlyList<SummaryVariable> Variables)
{
    public IReadOnlyList<LockTransfer> Locks { get; init; } = [];
    public IReadOnlyList<SummarySpawn> Spawns { get; init; } = [];
    public IReadOnlyList<SummaryThreadWork> ThreadWorks { get; init; } = [];
    public IReadOnlyList<SummaryJoin> Joins { get; init; } = [];
    public IReadOnlyList<SummaryWhenAll> WhenAlls { get; init; } = [];
    public IReadOnlyList<SummaryUnwrap> Unwraps { get; init; } = [];
    public IReadOnlyList<SummaryTimer> Timers { get; init; } = [];
}
