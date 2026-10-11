using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>What a driver run handed to code the analysis cannot follow — a call it does not follow or an operation the lowering does not
/// express —, to executions outside the member call, or to witness bodies. Every source is closed under <see cref="HeapReachability"/>.</summary>
public sealed class GenerationHandoffs
{
    private const string UNSAFE_OBJECT_CAST = "System.Runtime.CompilerServices.Unsafe.As<T>(object)";
    private const string OBJECT_GET_TYPE = "object.GetType()";
    private const string LOWERING = "lowering: ";
    private const string UNSUPPORTED = "unsupported";

    private readonly HeapSolution _heap;
    private readonly ExecutionAnalysis _analysis;
    private readonly DriverExecutions _executions;
    private readonly HeapReachability _reachability;
    private readonly HashSet<string> _inSetup = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outsideSetup = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outsideSetupBeyondUnsupported = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outsideSetupDirectly = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outsideSetupBeyondUnsupportedDirectly = new(StringComparer.Ordinal);
    private readonly HashSet<(string Instance, int Operation)> _unsupported = [];
    private readonly HashSet<string> _witnessedInSetup = new(StringComparer.Ordinal);
    private readonly HashSet<string> _witnessedOutsideSetup = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlySet<string>> _unseenMembersOutsideSetup = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foreignAccesses = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Instance, int Operation), IReadOnlySet<string>> _outsideByCall = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _unseen = new(StringComparer.Ordinal);

    /// <summary>Builds all handoff sets once for a completed generator run.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The completed scope run.</param>
    /// <param name="executions">The driver's execution classification.</param>
    /// <param name="reachability">The run's heap reachability.</param>
    public GenerationHandoffs(Driver driver, ScopeRun run, DriverExecutions executions, HeapReachability reachability)
    {
        if (run.Stopped)
            throw new ArgumentException("A stopped run has no handoffs to classify.", nameof(run));
        _heap = run.Heap!;
        _analysis = run.Executions!;
        _executions = executions;
        _reachability = reachability;

        var calls = new Dictionary<(string Instance, int Operation), IReadOnlySet<string>>();
        var direct = new Dictionary<(string Instance, int Operation), IReadOnlySet<string>>();
        var unknownCalls = UnfollowedCalls(run);
        foreach (var call in unknownCalls)
        {
            var values = call.Receivers.Concat(call.Arguments.SelectMany(argument => argument.Values)).Concat(call.Delegates);
            // A reference argument hands the cell or field it names, which the call may read and write as it may any storage of an
            // object handed to it: the object that storage lies in is handed (question 160).
            var storage = call.Arguments.SelectMany(argument => argument.References)
                              .SelectMany(target => ValueProvenance.ReferenceStorage(_heap, call.Instance, target, out _));
            AddCall((call.Instance.Id, call.OperationId), values.SelectMany(value => _heap.Resolve(call.Instance.Id, value)).Concat(storage));
        }

        var observation = new ValueObservation(driver, run, unknownCalls.Select(call => (call.Instance.Id, call.OperationId)).ToHashSet());
        foreach (var site in _heap.UnresolvedCallTargets)
        {
            // A delegate call's receiver the heap binds through a captured or ref cell, which the heap lists as unfollowed in the open
            // world; what a delegate no body resolves is handed reaches the unknown calls above as an unresolved dispatch.
            if (!_heap.Instances.TryGetValue(site.Instance, out var instance) ||
                instance.Summary.Calls.FirstOrDefault(call => call.OperationId == site.Operation) is not { } call ||
                instance.Summary.OpaqueCalls.Any(opaque => opaque.OperationId == site.Operation && IsTransparentIntrinsic(opaque.Callee)) ||
                call.Kind == IrCallKind.Delegate ||
                call.ReceiverSourceCalls.Count == 0 &&
                !call.ReceiverUnknownSources.Any(source => source is not (UnknownSource.Null or UnknownSource.Parameter)) &&
                !OpenParameter(instance, call, observation))
            {
                continue;
            }

            // The body the heap does not have runs on an object it does not name; a receiver it does name goes to the body it resolves.
            var arguments = call.Arguments.SelectMany(argument => argument.Values).SelectMany(value => _heap.Resolve(instance.Id, value)).ToArray();
            AddCall(site, call.Receivers.SelectMany(value => _heap.Resolve(instance.Id, value)).Concat(arguments), arguments);
        }

        // What an operation the lowering does not express is given goes where the analysis does not see, as an argument of a call it
        // does not follow does.
        foreach (var instance in _heap.Instances.Values)
        {
            foreach (var operation in instance.Summary.UnsupportedOperations)
            {
                _unsupported.Add((instance.Id, operation.OperationId));
                AddCall((instance.Id, operation.OperationId), operation.Values.SelectMany(value => _heap.Resolve(instance.Id, value)));
            }
        }

        foreach (var (site, regions) in calls)
            File(site, regions, direct[site], keepCall: true);

        foreach (var handoff in _heap.DelegateHandoffs)
        {
            var handed = new[] { handoff.RegionId }.Concat(handoff.Callees.SelectMany(Receipts)).ToArray();
            var inputs = Close(handed);
            var directInputs = _reachability.From(handed, captures: false);
            foreach (var site in handoff.Sites)
                File((site.CallerInstance, site.OperationId), inputs, directInputs, keepCall: false);
        }

        foreach (var handoff in _heap.StartupDelegates)
        {
            var handed = new[] { handoff.RegionId }.Concat(Receipts(handoff.CalleeInstance)).ToArray();
            File((handoff.CallerInstance, handoff.OperationId), Close(handed), _reachability.From(handed, captures: false), keepCall: false);
        }

        foreach (var instance in _heap.Instances.Values.Where(IsWitness))
        {
            var receipts = Close(instance.Receivers.Concat(instance.Parameters.Values.SelectMany(values => values)));
            if (FileWitness(instance.Id, receipts) && !ReadsByConvention(instance))
                _unseenMembersOutsideSetup[instance.Id] = instance.Receivers.ToHashSet(StringComparer.Ordinal);
        }

        var implementation = driver.Member.ContainingAssembly.Name;
        foreach (var access in _analysis.Accesses)
        {
            // A type initializer runs before any program code could put anything into its type's statics: what it reads or writes there
            // is the library's own start, never a value setup's seed stands for (question 158).
            var initializedStatic = _heap.Regions.TryGetValue(access.RegionId, out var accessed) && accessed.Kind == HeapRegionKind.Static
                ? $"type-initializer:{accessed.TypeKey}"
                : null;
            if (!_heap.Instances.TryGetValue(access.InstanceId, out var instance) ||
                !instance.BodyId.StartsWith($"body:{implementation}:", StringComparison.Ordinal) ||
                access.Executions.All(execution => _executions.Of(execution).Role is DriverExecutionRole.Setup or DriverExecutionRole.Own or
                                                                                      DriverExecutionRole.Child or DriverExecutionRole.Artefact ||
                                                   execution == initializedStatic))
            {
                continue;
            }

            _foreignAccesses.UnionWith(Close([access.RegionId]));
        }

        foreach (var action in new[] { DriverSynthesizer.CALL, DriverSynthesizer.ENUMERATE })
            _unseen[action] = UnseenIn(run, action);
        HandedOnlyToUnsupportedOutsideSetup = _outsideSetup.Where(region => !_outsideSetupBeyondUnsupported.Contains(region)).ToHashSet(StringComparer.Ordinal);
        HandedOnlyToUnsupportedOutsideSetupDirectly = _outsideSetupDirectly.Where(region => !_outsideSetupBeyondUnsupportedDirectly.Contains(region))
                                                                           .ToHashSet(StringComparer.Ordinal);

        void AddCall((string Instance, int Operation) site, IEnumerable<string> regions, IEnumerable<string>? directly = null)
        {
            var handed = regions.ToArray();
            var reached = directly?.ToArray() ?? handed;
            calls[site] = Close(calls.TryGetValue(site, out var existing) ? existing.Concat(handed) : handed);
            direct[site] = _reachability.From(direct.TryGetValue(site, out var existingDirect) ? existingDirect.Concat(reached) : reached, captures: false);
        }
    }

    /// <summary>Regions handed by a call instance, or an operation the lowering does not express, that ran in setup.</summary>
    public IReadOnlySet<string> HandedInSetup => _inSetup;

    /// <summary>Regions handed by a call instance, or an operation the lowering does not express, that ran outside setup.</summary>
    public IReadOnlySet<string> HandedOutsideSetup => _outsideSetup;

    /// <summary>For each call instance, or operation the lowering does not express, outside setup, the regions that site alone handed,
    /// closed under heap reachability.</summary>
    public IReadOnlyDictionary<(string Instance, int Operation), IReadOnlySet<string>> HandoffsOutsideSetup => _outsideByCall;

    /// <summary>The regions of <see cref="HandedOutsideSetup"/> that only operations the lowering does not express were handed: no call,
    /// delegate handoff or startup delegate handed them. Lowering those operations is what would make them seen.</summary>
    public IReadOnlySet<string> HandedOnlyToUnsupportedOutsideSetup { get; }

    /// <summary>The regions of <see cref="HandedOutsideSetup"/> reached from what was handed without entering what a delegate captures:
    /// code that holds a delegate can only run its body, which the analysis sees and judges by what it does.</summary>
    public IReadOnlySet<string> HandedOutsideSetupDirectly => _outsideSetupDirectly;

    /// <summary>The regions of <see cref="HandedOutsideSetupDirectly"/> that only operations the lowering does not express were
    /// handed.</summary>
    public IReadOnlySet<string> HandedOnlyToUnsupportedOutsideSetupDirectly { get; }

    /// <summary>Regions received by witness instances that ran in setup.</summary>
    public IReadOnlySet<string> WitnessedInSetup => _witnessedInSetup;

    /// <summary>Regions received by witness instances that ran outside setup.</summary>
    public IReadOnlySet<string> WitnessedOutsideSetup => _witnessedOutsideSetup;

    /// <summary>For each witness instance that ran outside setup standing for a member the analysis does not see — any but those
    /// <see cref="DriverSynthesizer.READ_WITNESS_FIELD"/> names —, the objects it ran on.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> UnseenMembersOutsideSetup => _unseenMembersOutsideSetup;

    /// <summary>Regions a library body loaded or stored in an execution outside setup, the call, enumeration and escape artefact.</summary>
    public IReadOnlySet<string> ForeignAccesses => _foreignAccesses;

    /// <summary>What the analysis did not see in the tree of <c>V_Call</c> or <c>V_Enum</c>, sorted: every unresolved dispatch of an
    /// instance running there, of any call kind, delegate calls included, and every dispatch without a receiver object in a body one
    /// runs; every reached body whose lowering was dropped, which has no
    /// instance to place in a tree and so counts in every tree; every unsupported operation of a body an instance running there has.
    /// Empty for any other action.</summary>
    /// <param name="action"><c>V_Call</c> or <c>V_Enum</c>.</param>
    public IReadOnlyList<string> Unseen(string action) => _unseen.GetValueOrDefault(action) ?? [];

    private IReadOnlyList<string> UnseenIn(ScopeRun run, string action)
    {
        var instances = _heap.Instances.Values.Where(instance => ExecutionsOf(instance.Id).Any(execution => _executions.InTree(execution, action)))
                             .ToArray();
        var ids = instances.Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
        var unseen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (instance, operation) in _heap.UnresolvedDispatches.Concat(_heap.UnresolvedCallTargets).Where(site => ids.Contains(site.Instance)))
            unseen.Add($"unresolved dispatch: {instance}#{operation}");
        var bodies = instances.Select(instance => instance.BodyId).ToHashSet(StringComparer.Ordinal);
        foreach (var (body, operation) in _heap.NoReceiverObjects.Where(site => bodies.Contains(site.BodyId)))
            unseen.Add($"no receiver object: {body}#{operation}");
        foreach (var dropped in run.LoweringDiagnostics.Where(diagnostic => diagnostic.StartsWith(LOWERING, StringComparison.Ordinal)))
            unseen.Add(dropped);
        foreach (var body in bodies)
        {
            if (run.Reachable.Bodies.TryGetValue(body, out var ir) &&
                ir.Blocks.SelectMany(block => block.Operations).Any(operation => operation is IrUnknownOperation { Reason: UNSUPPORTED }))
            {
                unseen.Add($"unsupported operation: {body}");
            }
        }

        return unseen.ToArray();
    }

    private IEnumerable<string> Receipts(string instanceId)
    {
        if (!_heap.Instances.TryGetValue(instanceId, out var instance))
            return [];
        return instance.Parameters.Values.SelectMany(values => values);
    }

    /// <summary>Files what one site handed by when it ran.</summary>
    /// <param name="site">The call instance or operation.</param>
    /// <param name="regions">What it handed, closed under heap reachability.</param>
    /// <param name="directly">What it handed, closed without entering what a delegate captures.</param>
    /// <param name="keepCall">Whether the site's own set is kept in <see cref="HandoffsOutsideSetup"/>.</param>
    private void File((string Instance, int Operation) site, IReadOnlySet<string> regions, IReadOnlySet<string> directly, bool keepCall)
    {
        var executions = ExecutionsOf(site.Instance);
        if (executions.Count != 0 && executions.All(_executions.IsArtefact))
            return;
        if (executions.Any(_executions.InSetup))
            _inSetup.UnionWith(regions);
        if (executions.Any(execution => !_executions.InSetup(execution) && !_executions.IsArtefact(execution)))
        {
            _outsideSetup.UnionWith(regions);
            _outsideSetupDirectly.UnionWith(directly);
            if (!_unsupported.Contains(site))
            {
                _outsideSetupBeyondUnsupported.UnionWith(regions);
                _outsideSetupBeyondUnsupportedDirectly.UnionWith(directly);
            }
            if (keepCall)
                _outsideByCall[site] = regions;
        }
    }

    /// <summary>Files what a witness instance received by when it ran.</summary>
    /// <param name="instanceId">The witness instance.</param>
    /// <param name="regions">What it received, closed under heap reachability.</param>
    /// <returns>Whether it ran outside setup.</returns>
    private bool FileWitness(string instanceId, IReadOnlySet<string> regions)
    {
        var executions = ExecutionsOf(instanceId);
        if (executions.Count != 0 && executions.All(_executions.IsArtefact))
            return false;
        if (executions.Any(_executions.InSetup))
            _witnessedInSetup.UnionWith(regions);
        if (!executions.Any(execution => !_executions.InSetup(execution) && !_executions.IsArtefact(execution)))
            return false;
        _witnessedOutsideSetup.UnionWith(regions);
        return true;
    }

    private IReadOnlySet<string> ExecutionsOf(string instanceId) =>
        _analysis.InstanceExecutions.GetValueOrDefault(instanceId) ?? new HashSet<string>(StringComparer.Ordinal);

    private IReadOnlySet<string> Close(IEnumerable<string> regions) => _reachability.From(regions);

    /// <summary>Whether a parameter the call's receiver may come from can hold an object the heap does not name: a caller binds it to
    /// a value <see cref="ValueObservation"/> finds unobserved, or no call edge binds it at all, because an execution the engine
    /// started entered the instance with what code nobody observed passed. In the driver's closed world every other parameter is
    /// bound through the heap's call edges, which the open-world rule of the heap does not know.</summary>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="call">The call whose receiver is judged.</param>
    /// <param name="observation">The run's observation owner.</param>
    private bool OpenParameter(MethodInstance instance, CallTransfer call, ValueObservation observation)
    {
        foreach (var ordinal in call.Receivers.Select(ParameterBase).OfType<int>().Distinct())
        {
            var bound = _heap.Edges.Any(edge => edge.CalleeInstance == instance.Id &&
                                                _heap.Instances[edge.CallerInstance].Summary.Calls
                                                     .Where(caller => caller.OperationId == edge.OperationId)
                                                     .SelectMany(caller => caller.Arguments).Any(argument => argument.ParameterOrdinal == ordinal));
            if (!bound || observation.HasUnobservedValues(instance, [new ParameterValue(ordinal)], ValueOrigin.None))
                return true;
        }
        return false;
    }

    /// <summary>The ordinal of the parameter a value is, or is a path from; <c>null</c> for any other value.</summary>
    /// <param name="value">The value.</param>
    private static int? ParameterBase(AbstractValue value) => value switch
    {
        ParameterValue parameter => parameter.Ordinal,
        PathValue path => ParameterBase(path.Base),
        _ => null
    };

    /// <summary>The engine's unresolved calls, excluding runtime identity casts that do not hand a value to unknown code.</summary>
    /// <param name="run">The completed generator run.</param>
    internal static IReadOnlyList<UnknownCall> UnfollowedCalls(ScopeRun run) =>
        UnknownCalls.Of(run.ScopeProgram!, run.Heap!).Where(call => !IsTransparentIntrinsic(call.Callee) && !TouchesNothing(call.Callee)).ToArray();

    /// <summary>Whether a runtime call preserves its argument's identity rather than handing it to unknown code.</summary>
    /// <param name="callee">The called member's display.</param>
    internal static bool IsTransparentIntrinsic(string callee) => string.Equals(callee, UNSAFE_OBJECT_CAST, StringComparison.Ordinal);

    /// <summary>Whether a runtime call with no body reads nothing of the object it runs on but its type, which never changes:
    /// <c>object.GetType()</c>. What it returns is still no object the heap names.</summary>
    /// <param name="callee">The called member's display.</param>
    private static bool TouchesNothing(string callee) => string.Equals(callee, OBJECT_GET_TYPE, StringComparison.Ordinal);

    private static bool IsWitness(MethodInstance instance) => instance.Summary.Stores.Any(store => IsWitnessField(store.Field));

    /// <summary>Whether an instance is a witness of a member the analysis takes to read what it gets — a getter, <c>ToString</c>, or
    /// a member of equality, hash or ordering, which a model's <c>calls</c> runs.</summary>
    /// <param name="instance">The instance.</param>
    public static bool IsConventionWitness(MethodInstance instance) => IsWitness(instance) && ReadsByConvention(instance);

    /// <summary>Whether a witness stands for a member the analysis takes to read what it gets: it stores only into fields
    /// <see cref="DriverSynthesizer.READ_WITNESS_FIELD"/> names.</summary>
    /// <param name="instance">The witness instance.</param>
    private static bool ReadsByConvention(MethodInstance instance) =>
        instance.Summary.Stores.Where(store => IsWitnessField(store.Field))
                .All(store => store.Field.Name.StartsWith(DriverSynthesizer.READ_WITNESS_FIELD, StringComparison.Ordinal));

    private static bool IsWitnessField(IrFieldRef field) =>
        field is { IsStatic: true, Assembly: DriverSynthesizer.ASSEMBLY, ContainingTypeId: DriverSynthesizer.WITNESSED_TYPE };
}
