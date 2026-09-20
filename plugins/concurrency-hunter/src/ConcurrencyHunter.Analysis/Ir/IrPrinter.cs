using System.Globalization;

namespace ConcurrencyHunter.Ir;

public static class IrPrinter
{
    public static string Print(IrBody body)
    {
        var lines = new List<string>
        {
            F($"body {Text(body.BodyId)} {body.Kind} owner={Text(body.OwnerSymbol)} " +
              $"method={Text(body.MethodSymbol)} schema={Text(body.SchemaVersion)} async={Boolean(body.IsAsync)} " +
              $"returns={Text(body.ReturnType)} async-iterator={Boolean(body.IsAsyncIterator)}")
        };

        foreach (var value in body.Values.OrderBy(value => value.Id))
        {
            lines.Add(F($"value {Value(value.Id)} {value.Kind} type={Text(value.Type)} " +
                        $"name={Text(value.Name)} ssa={N(value.SsaVersion)}"));
        }

        foreach (var region in body.Regions.OrderBy(region => region.Id))
        {
            lines.Add(F($"region r{N(region.Id)} {region.Kind} parent={Region(region.Parent)} " +
                        $"blocks={N(region.FirstBlockOrdinal)}..{N(region.LastBlockOrdinal)} " +
                        $"catch={OptionalText(region.CatchType)}"));
        }

        foreach (var block in body.Blocks.OrderBy(block => block.Ordinal))
        {
            lines.Add(F($"block b{N(block.Ordinal)} {block.Kind} region=r{N(block.Region)} " +
                        $"predecessors={Blocks(block.Predecessors)} " +
                        $"flow={FlowPredecessors(block.FlowPredecessors)}"));
            foreach (var operation in block.Operations)
                lines.Add(F($"operation {N(operation.Id)} {Operation(operation)} | {Provenance(operation.Provenance)}"));
            if (block.ConditionalBranch is not null)
                lines.Add("branch conditional " + Branch(block.ConditionalBranch));
            if (block.FallThroughBranch is not null)
                lines.Add("branch fall-through " + Branch(block.FallThroughBranch));
        }

        return string.Join('\n', lines) + "\n";
    }

    private static string Operation(IrOperation operation) => operation switch
    {
        IrAssignOperation assign => F($"assign {Value(assign.TargetValue)} <- {Value(assign.SourceValue)}"),
        IrPhiOperation phi => F($"phi {Value(phi.TargetValue)} <- " +
                                $"{string.Join(",", phi.Inputs.Select(PhiInput))}"),
        IrAllocateOperation allocate => F($"allocate {Value(allocate.ResultValue)} type={Text(allocate.AllocatedType)}"),
        IrLoadFieldOperation load => F($"load-field {Value(load.ResultValue)} <- " +
                                       $"{Location(load.ReceiverValue, load.Field)}"),
        IrStoreFieldOperation store => F($"store-field {Location(store.ReceiverValue, store.Field)} <- " +
                                          $"{Value(store.Value)} rmw={OperationId(store.ReadModifyWriteOf)}"),
        IrLoadElementOperation load => F($"load-element {Value(load.ResultValue)} <- " +
                                         $"{Value(load.ReceiverValue)}{Values(load.IndexValues)}"),
        IrStoreElementOperation store => F($"store-element {Value(store.ReceiverValue)}" +
                                            $"{Values(store.IndexValues)} <- {Value(store.Value)}"),
        IrCallOperation call => F($"call {call.CallKind} result={OptionalValue(call.ResultValue)} " +
                                   $"method={Text(call.Method)} receiver={OptionalValue(call.ReceiverValue)} " +
                                   $"arguments={Values(call.ArgumentValues)}{(call.IsAwaitedImmediately ? " awaited" : "")}"),
        IrCreateDelegateOperation create => F($"create-delegate {Value(create.ResultValue)} " +
                                                $"body={OptionalText(create.TargetBodyId)} " +
                                                $"method={OptionalText(create.TargetMethod)} " +
                                                $"receiver={OptionalValue(create.ReceiverValue)}"),
        IrCaptureOperation capture => F($"capture {Value(capture.Value)} body={Text(capture.TargetBodyId)}"),
        IrEscapeOperation escape => F($"escape {Value(escape.Value)} destination={Text(escape.Destination)}"),
        IrReturnOperation @return => F($"return {OptionalValue(@return.Value)}"),
        IrAwaitOperation awaitOperation => F($"await {Value(awaitOperation.AwaitableValue)} " +
                                             $"result={OptionalValue(awaitOperation.ResultValue)}" +
                                             (awaitOperation.TaskValue is int task ? $" task={Value(task)}" : "")),
        IrSpawnOperation spawn => F($"spawn {spawn.Kind} call={OperationId(spawn.CallOperationId)} " +
                                    $"handle={OptionalValue(spawn.HandleValue)} work={Values(spawn.WorkValues)} " +
                                    $"work-method={OptionalText(spawn.WorkMethod)} state={OptionalValue(spawn.StateValue)} " +
                                    $"antecedent={OptionalValue(spawn.AntecedentValue)} async={Boolean(spawn.WorkIsAsync)} " +
                                    $"awaits-work={Boolean(spawn.AwaitsWorkTask)} implicit-join={Boolean(spawn.JoinsOnReturn)}"),
        IrThreadWorkOperation threadWork => F($"thread-work {Value(threadWork.ThreadValue)} work={Value(threadWork.WorkValue)} " +
                                                  $"async={Boolean(threadWork.WorkIsAsync)}"),
        IrJoinOperation join => F($"join {join.Kind} call={OperationId(join.CallOperationId)} handles={Values(join.HandleValues)} " +
                                  $"handles-known={Boolean(join.HandlesKnown)} " +
                                  $"throws-only-after-completion={Boolean(join.ThrowsOnlyAfterCompletion)}"),
        IrWhenAllOperation whenAll => F($"when-all {Value(whenAll.ResultValue)} tasks={Values(whenAll.TaskValues)} " +
                                        $"tasks-known={Boolean(whenAll.TasksKnown)}"),
        IrUnwrapOperation unwrap => F($"unwrap {Value(unwrap.ResultValue)} <- {Value(unwrap.OuterValue)}"),
        IrTimerOperation timer => F($"timer {timer.Action} {Value(timer.TimerValue)} callback={OptionalValue(timer.CallbackValue)} " +
                                    $"state={OptionalValue(timer.StateValue)} due={Optional(timer.DueTime)} " +
                                    $"period={Optional(timer.Period)} wait-handle={OptionalValue(timer.WaitHandleValue)} " +
                                    $"result={OptionalValue(timer.ResultValue)} flag={Optional(timer.Flag)}"),
        IrAcquireOperation acquire => F($"acquire {acquire.Primitive} {acquire.Mode} {Value(acquire.LockValue)}"),
        IrReleaseOperation release => F($"release {release.Primitive} {release.Mode} {Value(release.LockValue)}"),
        IrAtomicOperation atomic => F($"atomic {Text(atomic.OperationKind)} {atomic.Effect} result={OptionalValue(atomic.ResultValue)} " +
                                       $"operands={Values(atomic.OperandValues)} target={OperationId(atomic.TargetOperationId)}"),
        IrComputeOperation compute => F($"compute {Value(compute.ResultValue)} {Text(compute.Operator)} " +
                                         $"operands={Values(compute.OperandValues)}"),
        IrCompareOperation compare => F($"compare {Value(compare.ResultValue)} {compare.Comparison} " +
                                         $"left={Value(compare.LeftValue)} right={OptionalValue(compare.RightValue)} " +
                                         $"type={OptionalText(compare.Type)}"),
        IrConvertOperation convert => F($"convert {Value(convert.ResultValue)} <- {Value(convert.OperandValue)} " +
                                         $"type={Text(convert.Type)} kind={convert.ConversionKind}"),
        IrUnknownOperation unknown => F($"unknown {Text(unknown.OperationKind)} reason={Text(unknown.Reason)} " +
                                         $"result={OptionalValue(unknown.ResultValue)} " +
                                         $"operands={Values(unknown.OperandValues)}"),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation.GetType().FullName)
    };

    private static string Branch(IrBranch branch) =>
        F($"{branch.Kind} destination={OptionalBlock(branch.Destination)} condition={OptionalValue(branch.ConditionValue)} " +
          $"jump-if-true={Boolean(branch.JumpIfTrue)} entering={Regions(branch.EnteringRegions)} " +
          $"leaving={Regions(branch.LeavingRegions)} finally={Regions(branch.FinallyRegions)}");

    private static string Provenance(IrProvenance provenance) =>
        F($"source={Text(provenance.Span.Path)}:{N(provenance.Span.StartLine)}:{N(provenance.Span.StartColumn)}-" +
          $"{N(provenance.Span.EndLine)}:{N(provenance.Span.EndColumn)} " +
          $"symbol={Text(provenance.ContainingSymbol)} syntax={Text(provenance.SyntaxKind)} " +
          $"transformation={Text(provenance.Transformation)}");

    private static string Location(int? receiver, IrFieldRef field) =>
        F($"{OptionalValue(receiver)}.{Text(field.ContainingType)}.{Text(field.Name)} " +
          $"assembly={Text(field.Assembly)} kind={field.Kind} static={Boolean(field.IsStatic)} " +
          $"readonly={Boolean(field.IsReadOnly)} type={Text(field.Type)}");

    private static string PhiInput(IrPhiInput input) =>
        F($"b{N(input.Predecessor.BlockOrdinal)}:{input.Predecessor.EdgeKind}={Value(input.Value)}");

    private static string Value(int value) => "%" + N(value);
    private static string OptionalValue(int? value) => value is int actual ? Value(actual) : "-";
    private static string OperationId(int? value) => value is int actual ? "operation:" + N(actual) : "-";
    private static string OptionalBlock(int? value) => value is int actual ? "b" + N(actual) : "-";
    private static string Region(int? value) => value is int actual ? "r" + N(actual) : "-";
    private static string Values(IEnumerable<int> values) => "[" + string.Join(",", values.Select(Value)) + "]";
    private static string Blocks(IEnumerable<int> values) => "[" + string.Join(",", values.Select(value => "b" + N(value))) + "]";
    private static string Regions(IEnumerable<int> values) => "[" + string.Join(",", values.Select(value => "r" + N(value))) + "]";
    private static string FlowPredecessors(IEnumerable<IrFlowPredecessor> predecessors) =>
        "[" + string.Join(",", predecessors.Select(predecessor =>
            F($"b{N(predecessor.BlockOrdinal)}:{predecessor.EdgeKind}"))) + "]";
    private static string Boolean(bool value) => value ? "true" : "false";
    private static string Boolean(bool? value) => value is bool actual ? Boolean(actual) : "-";
    private static string Optional<T>(T? value) where T : struct, Enum => value is T actual ? actual.ToString() : "-";
    private static string OptionalText(string? value) => value is null ? "-" : Text(value);
    private static string Text(string value) => "\"" + Escape(value) + "\"";
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                                                        .Replace("\r", "\\r", StringComparison.Ordinal)
                                                        .Replace("\n", "\\n", StringComparison.Ordinal)
                                                        .Replace("\"", "\\\"", StringComparison.Ordinal);
    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string F(string value) => value;
}
