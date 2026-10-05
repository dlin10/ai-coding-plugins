using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The one owner of whether a value of a generator run is observed: whether every object it may hold is one the heap names,
/// walking parameters back through the callers' arguments, source calls into their callees' returns, and paths to their bases.
/// <see cref="ValueProvenance"/> asks it before naming a value, and <see cref="GenerationHandoffs"/> before it excuses a call on a
/// parameter the heap binds.</summary>
internal sealed class ValueObservation
{
    private readonly Driver _driver;
    private readonly ScopeRun _run;
    private readonly HeapSolution _heap;
    private readonly IReadOnlySet<(string Instance, int Operation)> _unknownCalls;

    /// <summary>Reads observation from one completed generator run.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The completed run.</param>
    /// <param name="unknownCalls">The run's calls the engine cannot follow, by instance and operation.</param>
    public ValueObservation(Driver driver, ScopeRun run, IReadOnlySet<(string Instance, int Operation)> unknownCalls)
    {
        _driver = driver;
        _run = run;
        _heap = run.Heap!;
        _unknownCalls = unknownCalls;
    }

    /// <summary>Whether an instance is the member's own body entered by the driver's call or enumeration.</summary>
    /// <param name="instance">The instance.</param>
    public bool IsMemberEntry(MethodInstance instance) => instance.BodyId == IrLowering.RootBodyId(_driver.Member) &&
        _heap.Edges.Any(edge => edge.CalleeInstance == instance.Id && _heap.Instances[edge.CallerInstance].BodyId is { } body &&
                               (body == DriverBody(DriverSynthesizer.CALL) || body == DriverBody(DriverSynthesizer.ENUMERATE)));

    /// <summary>The body id of one driver action.</summary>
    /// <param name="action">The action.</param>
    public static string DriverBody(string action) => $"body:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.{action}";

    /// <summary>The regions a value holds, following parameters, the receiver, paths, allocations, source calls and returned
    /// references where the solved heap names none.</summary>
    /// <param name="instance">The instance expressing the value.</param>
    /// <param name="value">The symbolic value.</param>
    public IReadOnlySet<string> Resolve(MethodInstance instance, AbstractValue value) => Resolve(instance, value, []);

    /// <summary>Whether any alternative, or an unknown call among their origins, may hold an object the heap does not name.</summary>
    /// <param name="instance">The instance expressing the values.</param>
    /// <param name="values">The symbolic alternatives.</param>
    /// <param name="producers">Origins preserved through copies and merges.</param>
    public bool HasUnobservedValues(MethodInstance instance, IEnumerable<AbstractValue> values, ValueOrigin producers) =>
        HasUnobservedValues(instance, values, producers, []);

    /// <summary>Checks every alternative and the unknown calls in its origins; an origin is not an additional returned object.</summary>
    /// <param name="instance">The instance expressing the values.</param>
    /// <param name="values">The symbolic alternatives.</param>
    /// <param name="producers">Origins preserved through copies and merges.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobservedValues(MethodInstance instance, IEnumerable<AbstractValue> values, ValueOrigin producers,
                                      HashSet<(string Instance, AbstractValue Value)> seen) =>
        values.Any(value => HasUnobserved(instance, value, seen)) ||
        producers.Calls.Any(operation => _unknownCalls.Contains((instance.Id, operation)) && !IsDecidedCurrent(instance, operation));

    /// <summary>Checks source-call and argument alternatives without treating a nonempty points-to set as completeness.</summary>
    /// <param name="instance">The instance expressing the value.</param>
    /// <param name="value">The symbolic value.</param>
    /// <param name="seen">Alternatives already visited on this path.</param>
    private bool HasUnobserved(MethodInstance instance, AbstractValue value, HashSet<(string Instance, AbstractValue Value)> seen)
    {
        if (!seen.Add((instance.Id, value)))
            return false;
        try
        {
            if (value is ParameterValue parameter)
            {
                return _heap.Edges.Where(edge => edge.CalleeInstance == instance.Id).Any(edge =>
                {
                    var caller = _heap.Instances[edge.CallerInstance];
                    return caller.Summary.Calls.Where(call => call.OperationId == edge.OperationId).SelectMany(call => call.Arguments)
                                 .Where(argument => argument.ParameterOrdinal == parameter.Ordinal)
                                 .Any(argument => HasUnobservedValues(caller, argument.Values, argument.Producers, seen));
                });
            }
            if (value is CallResultValue call)
            {
                var opaque = instance.Summary.OpaqueCalls.Where(opaque => opaque.OperationId == call.OperationId).ToArray();
                // A decided `foreach` puts the alias of the storage it enumerates beside its Current placeholder, which is the
                // call's handle and not a value of its own, even where the call itself is one the engine cannot follow (ADR 0010,
                // TD-043); the alias is judged as itself.
                if (IsDecidedCurrent(instance, call.OperationId))
                    return false;
                if (_unknownCalls.Contains((instance.Id, call.OperationId)))
                    return true;
                if (opaque.Any(opaque => GenerationHandoffs.IsTransparentIntrinsic(opaque.Callee)))
                    return opaque.SelectMany(opaque => opaque.Arguments).Any(argument =>
                        argument.Values.Any(argumentValue => HasUnobserved(instance, argumentValue, seen)));
                var callees = _heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == call.OperationId)
                                   .Select(edge => _heap.Instances[edge.CalleeInstance]).ToArray();
                if (callees.Length != 0)
                    return callees.Any(callee => callee.Summary.Returns.Any(returned =>
                        HasUnobservedValues(callee, returned.Values, returned.Producers, seen)));
                // Recognizers and collection models put the result's alias beside this call placeholder in the summary values.
                if (opaque.Length != 0 && opaque.All(opaque => opaque.IsRecognized || opaque.Collection is not null || opaque.Implementations.Count != 0))
                    return false;
            }
            if (value is RefResultValue reference)
            {
                var callees = _heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == reference.OperationId)
                                   .Select(edge => _heap.Instances[edge.CalleeInstance]).ToArray();
                if (callees.Length != 0)
                    return callees.Any(callee => callee.Summary.RefParameters.Where(parameter => parameter.Ordinal == reference.Ordinal)
                                                     .SelectMany(parameter => parameter.Values).Any(returned => HasUnobserved(callee, returned, seen)));
            }
            if (value is PathValue path && HasUnobserved(instance, path.Base, seen))
                return true;
            return Resolve(instance, value).Count == 0 &&
                !(IsMemberEntry(instance) && (value is ConcurrencyHunter.Heap.ThisValue || value is PathValue { Base: ParameterValue }));
        }
        finally
        {
            seen.Remove((instance.Id, value));
        }
    }

    /// <summary>Whether the call is a <c>get_Current</c> of an enumeration whose <c>GetEnumerator</c> in the same body has a
    /// receiver, the enumeration the engine's summary decides and so names by an alias beside the call's placeholder.</summary>
    /// <param name="instance">The instance whose body makes the call.</param>
    /// <param name="operationId">The call's operation in that body.</param>
    private bool IsDecidedCurrent(MethodInstance instance, int operationId)
    {
        if (!_run.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
            return false;
        var calls = body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>().ToArray();
        return calls.FirstOrDefault(call => call.Id == operationId) is { EnumerationRole: IrEnumerationRole.Current, EnumerationId: int enumeration } &&
            calls.Any(call => call is { EnumerationRole: IrEnumerationRole.GetEnumerator, ReceiverValue: not null } && call.EnumerationId == enumeration);
    }

    private IReadOnlySet<string> Resolve(MethodInstance instance, AbstractValue value,
                                         HashSet<(string Instance, AbstractValue Value)> seen)
    {
        if (!seen.Add((instance.Id, value)))
            return new HashSet<string>(StringComparer.Ordinal);
        var solved = _heap.Resolve(instance.Id, value);
        if (solved.Count != 0)
            return solved;
        switch (value)
        {
            case ParameterValue parameter:
                return instance.Parameters.GetValueOrDefault(parameter.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            case ConcurrencyHunter.Heap.ThisValue:
                return instance.Receivers;
            case PathValue path:
            {
                IReadOnlySet<string> reached = Resolve(instance, path.Base, seen);
                foreach (var segment in path.Segments)
                    reached = reached.SelectMany(region => _heap.PointsTo(region, segment)).ToHashSet(StringComparer.Ordinal);
                return reached;
            }
            case AllocationValue allocation:
                return _heap.Regions.Values.Where(region => region.SiteBodyId == allocation.Site.BodyId &&
                                                            region.SiteOperationId == allocation.Site.OperationId)
                            .Select(region => region.Identity).ToHashSet(StringComparer.Ordinal);
            case CallResultValue call:
            {
                var result = new HashSet<string>(StringComparer.Ordinal);
                foreach (var intrinsic in instance.Summary.OpaqueCalls.Where(opaque => opaque.OperationId == call.OperationId &&
                    GenerationHandoffs.IsTransparentIntrinsic(opaque.Callee)))
                {
                    result.UnionWith(intrinsic.Arguments.SelectMany(argument => argument.Values)
                                             .SelectMany(argument => Resolve(instance, argument, seen)));
                }
                foreach (var edge in _heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == call.OperationId))
                {
                    if (!_heap.Instances.TryGetValue(edge.CalleeInstance, out var callee))
                        continue;
                    result.UnionWith(callee.Summary.Returns.SelectMany(returned => returned.Values)
                                                 .SelectMany(returned => Resolve(callee, returned, seen)));
                }
                return result;
            }
            case RefResultValue reference:
            {
                var result = new HashSet<string>(StringComparer.Ordinal);
                foreach (var edge in _heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == reference.OperationId))
                {
                    if (!_heap.Instances.TryGetValue(edge.CalleeInstance, out var callee))
                        continue;
                    result.UnionWith(callee.Summary.RefParameters.Where(parameter => parameter.Ordinal == reference.Ordinal)
                                                 .SelectMany(parameter => parameter.Values)
                                                 .SelectMany(returned => Resolve(callee, returned, seen)));
                }
                return result;
            }
            default:
                return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
