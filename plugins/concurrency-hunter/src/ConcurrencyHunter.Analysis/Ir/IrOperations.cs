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

public sealed record IrAllocateOperation(int Id, int ResultValue, string AllocatedType,
                                         IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => [];
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

public sealed record IrLoadElementOperation(int Id, int ResultValue, int ReceiverValue,
                                            IReadOnlyList<int> IndexValues, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => [ReceiverValue, .. IndexValues];
}

public sealed record IrStoreElementOperation(int Id, int ReceiverValue, IReadOnlyList<int> IndexValues,
                                             int Value, IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [ReceiverValue, .. IndexValues, Value];
}

public sealed record IrCallOperation(int Id, int? ResultValue, IrCallKind CallKind, string Method,
                                     int? ReceiverValue, IReadOnlyList<int> ArgumentValues,
                                     IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => ResultValue is int result ? [result] : [];
    public override IReadOnlyList<int> Operands => ReceiverValue is int receiver
        ? [receiver, .. ArgumentValues]
        : ArgumentValues;
}

public sealed record IrCreateDelegateOperation(int Id, int ResultValue, string? TargetBodyId,
                                               string? TargetMethod, int? ReceiverValue,
                                               IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [ResultValue];
    public override IReadOnlyList<int> Operands => ReceiverValue is int receiver ? [receiver] : [];
}

public sealed record IrCaptureOperation(int Id, int Value, string TargetBodyId, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [Value];
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
}

public sealed record IrAwaitOperation(int Id, int? ResultValue, int AwaitableValue, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => ResultValue is int result ? [result] : [];
    public override IReadOnlyList<int> Operands => [AwaitableValue];
}

public sealed record IrSpawnOperation(int Id, int? HandleValue, int WorkValue, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => HandleValue is int handle ? [handle] : [];
    public override IReadOnlyList<int> Operands => [WorkValue];
}

public sealed record IrJoinOperation(int Id, int HandleValue, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [HandleValue];
}

public sealed record IrAcquireOperation(int Id, int LockValue, IrSynchronizationPrimitive Primitive,
                                        IrLockMode Mode, IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [LockValue];
}

public sealed record IrReleaseOperation(int Id, int LockValue, IrSynchronizationPrimitive Primitive,
                                        IrLockMode Mode, IrProvenance Provenance) : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => [];
    public override IReadOnlyList<int> Operands => [LockValue];
}

public sealed record IrAtomicOperation(int Id, int? ResultValue, string OperationKind,
                                       IReadOnlyList<int> OperandValues, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => ResultValue is int result ? [result] : [];
    public override IReadOnlyList<int> Operands => OperandValues;
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
