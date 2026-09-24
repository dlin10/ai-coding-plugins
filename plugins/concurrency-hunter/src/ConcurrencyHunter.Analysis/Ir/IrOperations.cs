namespace ConcurrencyHunter.Ir;

public abstract record IrOperation(int Id, IrProvenance Provenance)
{
    public abstract IReadOnlyList<int> DefinedValues { get; }
    public abstract IReadOnlyList<int> Operands { get; }
}

public sealed record IrAssignOperation(int Id, int TargetValue, int SourceValue, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [TargetValue];
    public override IReadOnlyList<int> Operands => [SourceValue];
}

public sealed record IrPhiOperation(int Id, int TargetValue, IReadOnlyList<IrPhiInput> Inputs,
                                    IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [TargetValue];
    public override IReadOnlyList<int> Operands => Inputs.Select(input => input.Value).ToArray();
}

/// <summary><see cref="SiteOrdinal"/> is the 1-based source order of this <c>new</c> among those of the same created type in
/// the containing member, initializers and nested functions included; 0 when unknown.</summary>
public sealed record IrAllocateOperation(int Id, int ResultValue, string AllocatedType,
                                         IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => [];
    public string? AllocatedTypeKey { get; init; }
    public int SiteOrdinal { get; init; }

    /// <summary>The count a synchronization primitive was constructed with, when it is a constant: a <c>SemaphoreSlim</c> excludes
    /// only at one (TD-083). Null for every other allocation and for a count that is not constant.</summary>
    public int? SynchronizationCapacity { get; init; }

    /// <summary>How the collection this <c>new</c> creates compares the keys of its cells, read from the comparer it is given
    /// (ADR 0010). Null for every other allocation.</summary>
    public IrKeyEquality? KeyEquality { get; init; }
}

public sealed record IrLoadFieldOperation(int Id, int ResultValue, int? ReceiverValue, IrFieldRef Field,
                                          IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => ReceiverValue is int receiver ? [receiver] : [];
}

public sealed record IrStoreFieldOperation(int Id, int? ReceiverValue, IrFieldRef Field, int Value,
                                           int? ReadModifyWriteOf, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => ReceiverValue is int receiver ? [receiver, Value] : [Value];
}

/// <summary>The address of a field or element. Taking the address does not read or write its storage.</summary>
public sealed record IrAddressFieldOperation(int Id, int ResultValue, int? ReceiverValue, IrFieldRef Field,
                                             IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => ReceiverValue is int receiver ? [receiver] : [];
}

public sealed record IrAddressElementOperation(int Id, int ResultValue, int ReceiverValue, IReadOnlyList<int> IndexValues,
                                               IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => [ReceiverValue, .. IndexValues];
    public bool NamesOneCell { get; init; } = true;
}

public sealed record IrLoadReferenceOperation(int Id, int ResultValue, int AddressValue, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => [AddressValue];
}

public sealed record IrStoreReferenceOperation(int Id, int AddressValue, int Value, int? ReadModifyWriteOf,
                                               IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [AddressValue, Value];
}

/// <summary>
/// The types whose element and slice semantics the analysis knows: an index names the cell that many places along, and a slice
/// starts at the offset it names. Every other type has to prove it — a ref-returning indexer of a foreign type may hand back one
/// cell for every index, and a <c>Slice</c> of one may ignore the offset altogether — so its cells are unknown rather than
/// numbered (TD-043). A type of the compilation under analysis is never one of these, however it is named.
/// </summary>
public static class SpanTypes
{
    private static readonly HashSet<string> Known =
        new(["System.Span", "System.ReadOnlySpan", "System.Memory", "System.ReadOnlyMemory", "System.MemoryExtensions"],
            StringComparer.Ordinal);

    /// <summary>Whether the type named here is one of them. The name may be a metadata name or a display name, with or without
    /// its type arguments (<c>System.Span`1</c>, <c>System.Span&lt;int&gt;</c>), so that one set decides for the frontend and for
    /// the summaries alike.</summary>
    public static bool Names(string? type)
    {
        if (type is null)
            return false;
        var generic = type.IndexOfAny(['`', '<']);
        return Known.Contains(generic < 0 ? type : type[..generic]);
    }
}

public sealed record IrLoadElementOperation(int Id, int ResultValue, int ReceiverValue,
                                            IReadOnlyList<int> IndexValues, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => [ReceiverValue, .. IndexValues];

    /// <inheritdoc cref="IrStoreElementOperation.NamesOneCell"/>
    public bool NamesOneCell { get; init; } = true;
}

public sealed record IrStoreElementOperation(int Id, int ReceiverValue, IReadOnlyList<int> IndexValues,
                                             int Value, IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [ReceiverValue, .. IndexValues, Value];

    /// <summary>Whether each index names one cell of the receiver's storage, which an array does and which is proven of the types
    /// <see cref="SpanTypes"/> knows. A foreign type's ref-returning indexer is storage of its receiver all the same, but which
    /// cell it names is unknown (TD-043).</summary>
    public bool NamesOneCell { get; init; } = true;
}

/// <summary><see cref="ArgumentParameterOrdinals"/> names the target parameter of each of <see cref="ArgumentValues"/>;
/// <see cref="RefResults"/> maps the ordinal of a <c>ref</c> or <c>out</c> parameter to the new version of the local or
/// parameter passed there. <see cref="TargetMethodId"/> is the body id of the target's original definition, and the type
/// keys name the target as constructed at the call; all three are null or empty for a local function.</summary>
public sealed record IrCallOperation(int Id, int? ResultValue, IrCallKind CallKind, string Method,
                                     int? ReceiverValue, IReadOnlyList<int> ArgumentValues,
                                     IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => ResultValue is int result
        ? [result, .. RefResults.OrderBy(pair => pair.Key).Select(pair => pair.Value)]
        : RefResults.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
    public override IReadOnlyList<int> Operands => ReceiverValue is int receiver
        ? [receiver, .. ArgumentValues]
        : ArgumentValues;
    public IReadOnlyList<int> ArgumentParameterOrdinals { get; init; } = [];
    public IReadOnlyDictionary<int, int> RefResults { get; init; } = new Dictionary<int, int>();

    /// <summary>
    /// The value bound to the parameter with this ordinal, null where the call passes none. This is the only way to read an
    /// argument of a recognized member: a named argument may be written in any order, so the value standing first in the call
    /// is whichever parameter the caller chose to name first, and reading by position names a different parameter for every
    /// caller who writes them differently.
    /// </summary>
    public int? ArgumentAt(int ordinal)
    {
        for (var position = 0; position < ArgumentValues.Count; position++)
        {
            // A call the lowering recorded no ordinals for passed them in parameter order, which is what position means there.
            var bound = position < ArgumentParameterOrdinals.Count ? ArgumentParameterOrdinals[position] : position;
            if (bound == ordinal)
                return ArgumentValues[position];
        }

        return null;
    }

    /// <summary>The value bound to the last parameter this call passes one for, null where it passes none. "Last" is a fact
    /// about the parameters, not about the order the caller wrote them in.</summary>
    public int? LastArgument()
    {
        int? last = null;
        var highest = -1;
        for (var position = 0; position < ArgumentValues.Count; position++)
        {
            var bound = position < ArgumentParameterOrdinals.Count ? ArgumentParameterOrdinals[position] : position;
            if (bound > highest)
                (highest, last) = (bound, ArgumentValues[position]);
        }

        return last;
    }

    public string? TargetMethodId { get; init; }
    public string? TargetContainingTypeKey { get; init; }
    public IReadOnlyList<string> TargetMethodTypeArgumentKeys { get; init; } = [];
    public IrServiceCall? ServiceCall { get; init; }

    /// <summary>What the call does to the collection it is a member of, null for every other call (ADR 0010).</summary>
    public IrCollectionCall? Collection { get; init; }

    /// <summary>What the library semantics table says about the target, null for a target it does not describe (TD-034a). It
    /// decides only for a call that would otherwise be opaque: one whose dispatch may reach a source body runs that body.</summary>
    public IrLibraryCall? Library { get; init; }

    /// <summary>The call's result is awaited in the same expression, directly or through <c>ConfigureAwait</c>.</summary>
    public bool IsAwaitedImmediately { get; init; }

    public IrEnumerationRole EnumerationRole { get; init; }
    public int? EnumerationId { get; init; }
}

public enum IrEnumerationRole
{
    None,
    GetEnumerator,
    MoveNext,
    Current,
    Dispose
}

/// <summary>What a member of a modelled collection does to the collection's two resources (ADR 0010): its structure, which is the
/// collection's own path, and the storage of its cells. <see cref="KeyArgument"/> is the ordinal of the argument naming the one
/// cell the member touches; without it a member that touches cells touches all of them. <see cref="IsAtomic"/> is the
/// collection's own guarantee: a thread-safe collection performs each of its members atomically on both resources.</summary>
public sealed record IrCollectionCall(string Member, IrCollectionEffect Structure, IrCollectionEffect Element, int? KeyArgument,
                                      bool IsAtomic);

/// <summary>A member the library semantics table describes (TD-034a): its documentation id, whether the assembly it was found in
/// is inside the table's version range, and its effects on its arguments. Out of range the call stays opaque and is counted
/// apart.</summary>
public sealed record IrLibraryCall(string MemberId, bool InRange, IReadOnlyList<IrLibraryEffect> Effects);

/// <summary>What a known call does to the argument bound to the parameter with <see cref="ParameterOrdinal"/>.
/// <see cref="Arguments"/> are the values it does it to: the argument itself, or each element of a <c>params</c> array or slice
/// the call creates, without those whose type is immutable, since an effect on them touches nothing (R3).</summary>
public sealed record IrLibraryEffect(IrLibraryEffectKind Kind, int ParameterOrdinal)
{
    public IReadOnlyList<IrLibraryArgument> Arguments { get; init; } = [];
}

/// <summary>One value an effect applies to; a framework slice handed over ready is the storage it is cut from.</summary>
public sealed record IrLibraryArgument(int Value, bool IsSlice)
{
    /// <summary>Whether the parameter takes a sequence of objects rather than one: <c>entities</c> and not <c>entity</c>. An effect
    /// on it is an effect on the objects the sequence holds, and none on the sequence itself (R3).</summary>
    public bool IsSequence { get; init; }
}

public enum IrLibraryEffectKind
{
    /// <summary>A read of every field of every region reachable from the argument.</summary>
    DeepRead,

    /// <summary>A write of every field of the argument's own regions, one level deep.</summary>
    WriteArgument
}

/// <summary>What a locator or scope-creation call names: the constant service type key (null when the type is not a constant)
/// and the kind of provider its receiver is (null when its origin is none of <see cref="IrProviderKind"/>).</summary>
public sealed record IrServiceCall(IrServiceCallKind Kind, string? ServiceTypeKey, IrProviderKind? Provider)
{
    public string? Locator => ServiceTypeKey is null ? null : $"locator:{ServiceTypeKey}";
}

/// <summary><see cref="CapturedSymbolKeys"/> are the variables the target body captures; the target members are as on
/// <see cref="IrCallOperation"/>, null for a lambda or a local function.</summary>
public sealed record IrCreateDelegateOperation(int Id, int ResultValue, string? TargetBodyId,
                                               string? TargetMethod, int? ReceiverValue,
                                               IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => ReceiverValue is int receiver ? [receiver] : [];
    public IReadOnlyList<string> CapturedSymbolKeys { get; init; } = [];
    public int SiteOrdinal { get; init; }
    public string? TargetMethodId { get; init; }
    public string? TargetContainingTypeKey { get; init; }
    public IReadOnlyList<string> TargetMethodTypeArgumentKeys { get; init; } = [];

    /// <summary>The method group names a virtual member through <c>base</c>: the delegate runs <see cref="TargetMethodId"/> itself,
    /// never an override of it, as a base call does.</summary>
    public bool IsNonVirtual { get; init; }
}

public sealed record IrCaptureOperation(int Id, int Value, string TargetBodyId, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [Value];
    public string? SymbolKey { get; init; }
}

public sealed record IrEscapeOperation(int Id, int Value, string Destination, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [Value];
}

public sealed record IrReturnOperation(int Id, int? Value, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => Value is int value ? [value] : [];
    public bool IsByRef { get; init; }
}

/// <summary>
/// An operation at which the thread running this body may change. Everything that suspends is one: the continuation after an
/// <c>await</c> and the resumption of an iterator after a <c>yield return</c> may each run on a thread other than the one that
/// reached it, which breaks every primitive owned by the thread that took it. An analysis asking "does this hold across a
/// suspension" asks for this, never for one kind of it, so a suspension the lowering learns later is counted without any
/// consumer changing (R3, TD-083).
/// </summary>
public interface IIrSuspension
{
}

/// <summary>An await joins the task it awaits and throws only once that task is complete. <see cref="TaskValue"/> is the task
/// when the awaitable is its <c>ConfigureAwait</c> result; otherwise the awaitable itself is the task.</summary>
public sealed record IrAwaitOperation(int Id, int? ResultValue, int AwaitableValue, IrProvenance Provenance)
    : IrOperation(Id, Provenance), IIrSuspension
{
    public override IReadOnlyList<int> DefinedValues => ResultValue is int result ? [result] : [];
    public override IReadOnlyList<int> Operands => TaskValue is int task ? [AwaitableValue, task] : [AwaitableValue];
    public int? TaskValue { get; init; }
}

/// <summary>A <c>yield return</c>: the iterator hands a value to whoever is enumerating it and stops there until asked for the
/// next one, which that caller may ask for from another thread.</summary>
public sealed record IrYieldOperation(int Id, int? Value, IrProvenance Provenance)
    : IrOperation(Id, Provenance), IIrSuspension
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => Value is int value ? [value] : [];
}

/// <summary>Work a BCL call starts; it follows its call, <see cref="CallOperationId"/>, and defines nothing: the handle is the
/// call's result or, for <see cref="IrSpawnKind.ThreadStart"/>, the started <c>Thread</c>, whose work an
/// <see cref="IrThreadWorkOperation"/> binds. Each work value is a delegate the spawn invokes, an array of such delegates
/// the call does not list, or, with <see cref="WorkMethod"/>, an object the spawn calls that method on. <see cref="AwaitsWorkTask"/> (set by the overload, whatever the work's body) says the call's handle
/// completes only after the task an async work returns; <see cref="JoinsOnReturn"/> says the call itself returns, and throws,
/// only after every iteration is complete.</summary>
public sealed record IrSpawnOperation(int Id, IrSpawnKind Kind, int CallOperationId, int? HandleValue,
                                      IReadOnlyList<int> WorkValues, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands =>
        [.. new[] { HandleValue }.OfType<int>(), .. WorkValues, .. new[] { StateValue, AntecedentValue }.OfType<int>()];
    public int? StateValue { get; init; }
    public int? AntecedentValue { get; init; }
    public string? WorkMethod { get; init; }
    public bool WorkIsAsync { get; init; }
    public bool AwaitsWorkTask { get; init; }
    public bool JoinsOnReturn { get; init; }
}

/// <summary>A <c>Thread</c> constructor binding the work its <c>Start</c> runs; a thread never waits for the task an async
/// work returns.</summary>
public sealed record IrThreadWorkOperation(int Id, int ThreadValue, int WorkValue, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [ThreadValue, WorkValue];
    public bool WorkIsAsync { get; init; }
}

/// <summary>A BCL call, <see cref="CallOperationId"/>, that returns only after its handles complete; unlike an await it may
/// throw before they do. Handles are unknown when the call's tasks are not listed in the call itself.</summary>
public sealed record IrJoinOperation(int Id, IrJoinKind Kind, int CallOperationId, IReadOnlyList<int> HandleValues,
                                     bool HandlesKnown, IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => HandleValues;
    public bool ThrowsOnlyAfterCompletion => false;
}

/// <summary>The task <c>Task.WhenAll</c> returned, <see cref="ResultValue"/>, completes after <see cref="TaskValues"/>;
/// those are unknown when the tasks are not listed in the call itself.</summary>
public sealed record IrWhenAllOperation(int Id, int ResultValue, IReadOnlyList<int> TaskValues, bool TasksKnown,
                                        IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [ResultValue, .. TaskValues];
}

/// <summary><c>Unwrap()</c>: <see cref="ResultValue"/> is the handle of the task the outer task's work returned.</summary>
public sealed record IrUnwrapOperation(int Id, int ResultValue, int OuterValue, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [ResultValue, OuterValue];
}

/// <summary>A step in a timer's life. <c>System.Threading.Timer</c>: <see cref="IrTimerAction.Create"/> with callback, state,
/// due time and period, <see cref="IrTimerAction.Change"/>, the disposals; <c>System.Timers.Timer</c>: the <c>Elapsed</c>
/// subscription with its handler as callback, <c>AutoReset</c> and <c>Enabled</c> writes with their flag, <c>Start</c>,
/// <c>Stop</c>. <see cref="ResultValue"/> is the task <c>DisposeAsync</c> returns.</summary>
public sealed record IrTimerOperation(int Id, IrTimerAction Action, int TimerValue, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands =>
        new int?[] { TimerValue, CallbackValue, StateValue, WaitHandleValue, ResultValue }.OfType<int>().ToArray();
    public int? CallbackValue { get; init; }
    public int? StateValue { get; init; }
    public IrTimerInterval? DueTime { get; init; }
    public IrTimerInterval? Period { get; init; }
    public int? WaitHandleValue { get; init; }
    public int? ResultValue { get; init; }
    public IrTimerFlag? Flag { get; init; }
}

/// <summary>An entry into a synchronization primitive. An unconditional entry holds the primitive once control goes on normally;
/// a conditional one, which is every entry with a timeout, holds it only where <see cref="ConditionValue"/> is true (TD-083).</summary>
public sealed record IrAcquireOperation(int Id, int LockValue, IrSynchronizationPrimitive Primitive,
                                        IrLockMode Mode, IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => ConditionValue is int condition ? [LockValue, condition] : [LockValue];

    /// <summary>The value whose truth proves the entry succeeded; null for an unconditional entry.</summary>
    public int? ConditionValue { get; init; }
}

public sealed record IrReleaseOperation(int Id, int LockValue, IrSynchronizationPrimitive Primitive,
                                        IrLockMode Mode, IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [LockValue];

    /// <summary>How many permits the exit gives back. Every primitive that is taken and given back once gives back one, and so
    /// does a <c>SemaphoreSlim.Release()</c>; <c>Release(n)</c> gives back the count it names, and null is a count that is not a
    /// constant. An exit that gives back more than it took leaves the primitive open to more than one holder (TD-083).</summary>
    public int? Permits { get; init; } = 1;
}

/// <summary>The mark that makes a field load or store atomic on its cell (TD-082), added after it the way the BCL marks follow a
/// call. <see cref="OperationKind"/> names what it came from, <see cref="Effect"/> what it does to the cell, and
/// <see cref="TargetOperationId"/> is the load or store it marks; a mark without one names no cell and is no access.</summary>
public sealed record IrAtomicOperation(int Id, int? ResultValue, string OperationKind,
                                       IReadOnlyList<int> OperandValues, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => ResultValue is int result ? [result] : [];
    public override IReadOnlyList<int> Operands => ComparandValue is int comparand ? [.. OperandValues, comparand] : OperandValues;
    public IrAtomicEffect Effect { get; init; }
    public int? TargetOperationId { get; init; }

    /// <summary>The value the cell must still hold for a <see cref="IrAtomicEffect.CompareAndSwap"/> to write, null for every
    /// other effect. What that value was read from is what says whether the operation verifies the read its own value was built
    /// from, which is the only thing that makes a sequence around it atomic (R1).</summary>
    public int? ComparandValue { get; init; }
}

public sealed record IrComputeOperation(int Id, int ResultValue, string Operator,
                                        IReadOnlyList<int> OperandValues, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => OperandValues;
}

public sealed record IrCompareOperation(int Id, int ResultValue, IrComparisonKind Comparison,
                                        int LeftValue, int? RightValue, string? Type,
                                        IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => RightValue is int right ? [LeftValue, right] : [LeftValue];

    /// <summary>Which way the comparison runs, null where the lowering does not record it. A predicate is built only from a
    /// comparison that says it (TD-090).</summary>
    public IrComparisonOperator? Operator { get; init; }
}

public sealed record IrConvertOperation(int Id, int ResultValue, int OperandValue, string Type,
                                        IrConversionKind ConversionKind, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => [OperandValue];
}

public sealed record IrUnknownOperation(int Id, int? ResultValue, string OperationKind, string Reason,
                                        IReadOnlyList<int> OperandValues, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => ResultValue is int result ? [result] : [];
    public override IReadOnlyList<int> Operands => OperandValues;
}
