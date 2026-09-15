using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Ir;

public static class IrSchema
{
    public const string VERSION = "1.0";
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

public enum IrSynchronizationPrimitive
{
    Monitor
}

public enum IrLockMode
{
    Exclusive,
    Read,
    Write,
    UpgradeableRead
}

public enum IrComparisonKind
{
    Equality,
    Ordering,
    Null,
    Type
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
                            IReadOnlyList<IrRegion> Regions, string SchemaVersion = IrSchema.VERSION);

public sealed record IrValue(int Id, IrValueKind Kind, string Type, string Name, int SsaVersion);

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
}

public sealed record IrPhiInput(IrFlowPredecessor Predecessor, int Value);
