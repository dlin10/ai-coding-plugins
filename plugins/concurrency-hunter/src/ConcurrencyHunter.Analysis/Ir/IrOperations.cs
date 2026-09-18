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
    public string? TargetMethodId { get; init; }
    public string? TargetContainingTypeKey { get; init; }
    public IReadOnlyList<string> TargetMethodTypeArgumentKeys { get; init; } = [];
    public IrServiceCall? ServiceCall { get; init; }

    /// <summary>The call's result is awaited in the same expression, directly or through <c>ConfigureAwait</c>.</summary>
    public bool IsAwaitedImmediately { get; init; }
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
}

/// <summary>An await joins the task it awaits and throws only once that task is complete. <see cref="TaskValue"/> is the task
/// when the awaitable is its <c>ConfigureAwait</c> result; otherwise the awaitable itself is the task.</summary>
public sealed record IrAwaitOperation(int Id, int? ResultValue, int AwaitableValue, IrProvenance Provenance)
    : IrOperation(Id, Provenance)
{
    public override IReadOnlyList<int> DefinedValues => ResultValue is int result ? [result] : [];
    public override IReadOnlyList<int> Operands => TaskValue is int task ? [AwaitableValue, task] : [AwaitableValue];
    public int? TaskValue { get; init; }
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
