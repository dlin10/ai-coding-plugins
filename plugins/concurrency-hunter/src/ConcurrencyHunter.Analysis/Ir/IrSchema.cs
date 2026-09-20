using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Ir;

public static class IrSchema
{
    public const string VERSION = "1.2";
}

public enum IrRefKind
{
    None,
    Ref,
    Out,
    In,
    RefReadOnly
}

public enum IrBodyKind
{
    Method,
    Lambda,
    LocalFunction
}

public enum IrValueKind
{
    Parameter,
    Receiver,
    Local,
    Temporary,
    Constant
}

public enum IrBlockKind
{
    Entry,
    Block,
    Exit
}

/// <summary><see cref="Exceptional"/> leads from a block inside a <c>try</c> to a handler: the block may be left at any of its
/// operations, so none of them is known to have run on that edge.</summary>
public enum IrEdgeKind
{
    Explicit,
    FinallyEntry,
    FinallyExit,
    Exceptional
}

public enum IrBranchKind
{
    None,
    Regular,
    Return,
    StructuredExceptionHandling,
    ProgramTermination,
    Throw,
    Rethrow,
    Error
}

public enum IrRegionKind
{
    Root,
    LocalLifetime,
    StaticLocalInitializer,
    ErroneousBody,
    TryAndFinally,
    TryAndCatch,
    FilterAndHandler,
    Filter,
    Try,
    Catch,
    Finally
}

public enum IrFieldKind
{
    Field,
    PropertyBackingField,
    PrimaryConstructorParameter
}

public enum IrCallKind
{
    Static,
    Instance,
    Virtual,
    Interface,
    Delegate,
    LocalFunction,
    Constructor
}

/// <summary>A service-locator call (<c>GetService</c>, <c>GetRequiredService</c>), a <c>GetServices</c> call, or a
/// <c>CreateScope</c>/<c>CreateAsyncScope</c> call.</summary>
public enum IrServiceCallKind
{
    Locator,
    LocatorAll,
    ScopeCreation
}

/// <summary>What a collection member does to one of the collection's two resources (ADR 0010).</summary>
public enum IrCollectionEffect
{
    None,
    Read,
    Write,
    ReadWrite
}

/// <summary>How a collection compares the keys of its cells, read from the comparer it was constructed with: by value, ignoring
/// case, or by a comparer the analysis cannot read, under which no two keys are proven apart.</summary>
public enum IrKeyEquality
{
    Value,
    IgnoreCase,
    Unknown
}

/// <summary>Where a service provider receiver syntactically comes from.</summary>
public enum IrProviderKind
{
    RequestServices,
    HostServices,
    ApplicationServices,
    ScopeServiceProvider,
    InjectedProvider
}

/// <summary>The synchronization mechanisms the lowering models (TD-080, TD-083). Two mechanisms on one object are independent, so
/// the kind takes part in matching a release to its acquisition and in the verdict of a pair.</summary>
public enum IrSynchronizationPrimitive
{
    Monitor,
    Lock,
    Mutex,
    SemaphoreSlim,
    ReaderWriterLockSlim
}

public enum IrLockMode
{
    Exclusive,
    Read,
    Write,
    UpgradeableRead
}

/// <summary>How a BCL call starts work. <see cref="AsyncCall"/> and <see cref="AsyncVoid"/> are decided on resolved call edges
/// from <see cref="IrCallOperation.IsAwaitedImmediately"/> and the callee's <see cref="IrBody.IsAsync"/>, not by the lowering;
/// <see cref="Unrecognized"/> is a call of a recognized BCL type in a form the lowering does not model, whose delegates run
/// unordered.</summary>
public enum IrSpawnKind
{
    TaskRun,
    StartNew,
    ContinueWith,
    QueueUserWorkItem,
    UnsafeQueueUserWorkItem,
    ThreadStart,
    ParallelFor,
    ParallelForEach,
    ParallelForEachAsync,
    AsyncCall,
    AsyncVoid,
    Unrecognized
}

/// <summary>A BCL call that returns only once its handles are complete.</summary>
public enum IrJoinKind
{
    Wait,
    Join,
    WaitAll,
    WaitOne
}

public enum IrTimerAction
{
    Create,
    Change,
    Dispose,
    DisposeWaitHandle,
    DisposeAsync,
    ElapsedSubscribe,
    SetAutoReset,
    SetEnabled,
    Start,
    Stop
}

/// <summary>A timer's due time or period: <see cref="Unknown"/> is any value that is not a constant, a <c>TimeSpan</c> included.</summary>
public enum IrTimerInterval
{
    Zero,
    Infinite,
    Positive,
    Unknown
}

public enum IrTimerFlag
{
    True,
    False,
    Unknown
}

/// <summary>What an atomic operation does to the cell it names (TD-082): <c>Volatile.Read</c> and a <c>volatile</c> field read are
/// <see cref="Read"/>, <c>Volatile.Write</c> and a <c>volatile</c> field write <see cref="Write"/>, and the <c>Interlocked</c>
/// members that replace a value <see cref="ReadModifyWrite"/>. <see cref="CompareAndSwap"/> is the one that writes only where the
/// cell still holds the value it is given, which is what lets it carry a value an earlier read produced without losing what
/// happened in between (R1).</summary>
public enum IrAtomicEffect
{
    Read,
    Write,
    ReadModifyWrite,
    CompareAndSwap
}

public enum IrComparisonKind
{
    Equality,
    Ordering,
    Null,
    Type
}

/// <summary>Which way a comparison runs. The kind alone does not say it: <c>==</c> and <c>!=</c> are both equality, and a path
/// predicate built from one without the other would claim the opposite of what the branch tests (TD-090).</summary>
public enum IrComparisonOperator
{
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual
}

public enum IrConversionKind
{
    Identity,
    Reference,
    Boxing,
    Unboxing,
    Numeric,
    UserDefined,
    Other
}

public sealed record IrBody(string BodyId, IrBodyKind Kind, string OwnerSymbol, string MethodSymbol,
                            IReadOnlyList<IrValue> Values, IReadOnlyList<IrBlock> Blocks,
                            IReadOnlyList<IrRegion> Regions, string SchemaVersion = IrSchema.VERSION)
{
    /// <summary>The body's parameters with their incoming value; the SSA versions of a parameter share its value's
    /// <see cref="IrValue.SymbolKey"/>, so the value a <c>ref</c> or <c>out</c> parameter holds when the body ends can be read.</summary>
    public IReadOnlyList<IrParameter> Parameters { get; init; } = [];

    public bool IsAsync { get; init; }
    public string ReturnType { get; init; } = "void";
    public bool IsAsyncIterator { get; init; }
}

/// <summary><see cref="SymbolKey"/> identifies the local or parameter a value is a version of, across bodies: the id of the
/// body declaring it, its name and its declaration's span start, or <c>this</c> for the receiver.</summary>
public sealed record IrValue(int Id, IrValueKind Kind, string Type, string Name, int SsaVersion)
{
    public string? SymbolKey { get; init; }
}

public sealed record IrParameter(string Name, string Type, IrRefKind RefKind, int Ordinal, int Value);

public sealed record IrFlowPredecessor(int BlockOrdinal, IrEdgeKind EdgeKind);

public sealed record IrBranch(IrBranchKind Kind, int? Destination, int? ConditionValue, bool? JumpIfTrue,
                              IReadOnlyList<int> EnteringRegions, IReadOnlyList<int> LeavingRegions,
                              IReadOnlyList<int> FinallyRegions);

public sealed record IrBlock(int Ordinal, IrBlockKind Kind, int Region, IReadOnlyList<int> Predecessors,
                             IReadOnlyList<IrFlowPredecessor> FlowPredecessors,
                             IReadOnlyList<IrOperation> Operations, IrBranch? ConditionalBranch,
                             IrBranch? FallThroughBranch);

public sealed record IrRegion(int Id, IrRegionKind Kind, int? Parent, int FirstBlockOrdinal,
                              int LastBlockOrdinal, string? CatchType);

public sealed record IrProvenance(SourceSpan Span, string ContainingSymbol, string SyntaxKind,
                                  string Transformation);

/// <summary><see cref="ContainingTypeIdentity"/> identifies the containing type beside <see cref="Assembly"/> when it differs from
/// the display name <see cref="ContainingType"/>: in a closed generic, each type argument carries its own assembly.</summary>
public sealed record IrFieldRef(string Assembly, string ContainingType, string Name, IrFieldKind Kind,
                                bool IsStatic, bool IsReadOnly, string Type, string? ContainingTypeIdentity = null)
{
    public string ContainingTypeId => ContainingTypeIdentity ?? ContainingType;

    /// <summary>The field is declared <c>volatile</c>, so each of its reads and writes is atomic on its cell (TD-082).</summary>
    public bool IsVolatile { get; init; }
}

public sealed record IrPhiInput(IrFlowPredecessor Predecessor, int Value);
