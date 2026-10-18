using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
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
    private readonly SortedSet<string> _unfollowedReceivers = new(StringComparer.Ordinal);
    private readonly StageChecks _checks;

    /// <summary>Builds all handoff sets once for a completed generator run.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The completed scope run.</param>
    /// <param name="executions">The driver's execution classification.</param>
    /// <param name="reachability">The run's heap reachability.</param>
    public GenerationHandoffs(Driver driver, ScopeRun run, DriverExecutions executions, HeapReachability reachability)
        : this(driver, run, executions, reachability, default)
    {
    }

    /// <summary>Builds all handoff sets once for a completed generator run, as work of a generation's stage.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The completed scope run.</param>
    /// <param name="executions">The driver's execution classification.</param>
    /// <param name="reachability">The run's heap reachability.</param>
    /// <param name="checks">The stage's checks, which cancel the building at each call, instance, access and edge it goes through.</param>
    internal GenerationHandoffs(Driver driver, ScopeRun run, DriverExecutions executions, HeapReachability reachability, StageChecks checks)
    {
        if (run.Stopped)
            throw new ArgumentException("A stopped run has no handoffs to classify.", nameof(run));
        _heap = run.Heap!;
        _analysis = run.Executions!;
        _executions = executions;
        _reachability = reachability;
        _checks = checks;

        var calls = new Dictionary<(string Instance, int Operation), IReadOnlySet<string>>();
        var direct = new Dictionary<(string Instance, int Operation), IReadOnlySet<string>>();
        var unknownCalls = UnfollowedCalls(run);
        foreach (var call in checks.Each(unknownCalls))
        {
            var values = call.Receivers.Concat(call.Arguments.SelectMany(argument => argument.Values)).Concat(call.Delegates);
            // A reference argument hands the cell or field it names, which the call may read and write as it may any storage of an
            // object handed to it: the object that storage lies in is handed (question 160).
            var storage = checks.Each(call.Arguments.SelectMany(argument => argument.References))
                                .SelectMany(target => ValueProvenance.ReferenceStorage(_heap, call.Instance, target, out _));
            AddCall((call.Instance.Id, call.OperationId), checks.Each(values).SelectMany(value => _heap.Resolve(call.Instance.Id, value)).Concat(storage));
        }

        var observation = new ValueObservation(driver, run, checks.Each(unknownCalls).Select(call => (call.Instance.Id, call.OperationId)).ToHashSet(), checks);
        foreach (var site in checks.Each(_heap.UnresolvedCallTargets))
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
            // That body may be a user's override, which reaches a named receiver only through what a user can name: what the library
            // keeps in a field a user cannot is not handed (question 142).
            var arguments = checks.Each(call.Arguments.SelectMany(argument => argument.Values)).SelectMany(value => _heap.Resolve(instance.Id, value)).ToArray();
            AddCall(site, arguments, arguments, checks.Each(call.Receivers).SelectMany(value => _heap.Resolve(instance.Id, value)));
        }

        // What an operation the lowering does not express is given goes where the analysis does not see, as an argument of a call it
        // does not follow does.
        foreach (var instance in checks.Each(_heap.Instances.Values))
        {
            foreach (var operation in checks.Each(instance.Summary.UnsupportedOperations))
            {
                _unsupported.Add((instance.Id, operation.OperationId));
                AddCall((instance.Id, operation.OperationId), checks.Each(operation.Values).SelectMany(value => _heap.Resolve(instance.Id, value)));
            }
        }

        foreach (var (site, regions) in checks.Each(calls))
            File(site, regions, direct[site], keepCall: true);

        foreach (var handoff in checks.Each(_heap.DelegateHandoffs))
        {
            var handed = new[] { handoff.RegionId }.Concat(checks.Each(handoff.Callees).SelectMany(Receipts)).ToArray();
            var inputs = Close(handed);
            var directInputs = _reachability.From(handed, captures: false);
            foreach (var site in checks.Each(handoff.Sites))
                File((site.CallerInstance, site.OperationId), inputs, directInputs, keepCall: false);
        }

        foreach (var handoff in checks.Each(_heap.StartupDelegates))
        {
            var handed = new[] { handoff.RegionId }.Concat(Receipts(handoff.CalleeInstance)).ToArray();
            File((handoff.CallerInstance, handoff.OperationId), Close(handed), _reachability.From(handed, captures: false), keepCall: false);
        }

        foreach (var instance in checks.Each(_heap.Instances.Values).Where(IsWitness))
        {
            var received = Received(_heap, instance);
            // A witness stands for a user's override, which reaches what it is given only through what a user can name (question 142).
            var receipts = _reachability.VisibleFrom(received.Receivers.Concat(received.Parameters.Values.SelectMany(values => values)));
            if (FileWitness(instance.Id, receipts) && !ReadsByConvention(instance))
                _unseenMembersOutsideSetup[instance.Id] = received.Receivers;
        }

        var implementation = driver.Member.ContainingAssembly.Name;
        var makers = checks.Each(_heap.Instances.Values).ToLookup(instance => (instance.BodyId, instance.Context));
        // An object a type initializer made while it ran is its own: no program code could have handed it anything yet (question 142).
        bool MadeThere(string regionId, string execution) =>
            execution.StartsWith("type-initializer:", StringComparison.Ordinal) && _heap.Regions.TryGetValue(regionId, out var region) &&
            region.SiteBodyId is { } site && makers[(site, region.Context)].Select(maker => ExecutionsOf(maker.Id)).ToArray() is { Length: > 0 } made &&
            made.All(executions => executions.Count != 0 && executions.All(one => one == execution));
        foreach (var access in checks.Each(_analysis.Accesses))
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
                                                   execution == initializedStatic || MadeThere(access.RegionId, execution)))
            {
                continue;
            }

            _foreignAccesses.UnionWith(Close([access.RegionId]));
        }

        var unfollowed = checks.Each(unknownCalls).ToLookup(call => call.Instance.Id, call => call.Callee, StringComparer.Ordinal);
        var edges = checks.Each(_heap.Edges).ToLookup(edge => edge.CallerInstance, edge => edge.CalleeInstance, StringComparer.Ordinal);
        var program = run.ScopeProgram!.Program;
        foreach (var instance in checks.Each(_heap.Instances.Values).Where(instance => OutsideSetup(ExecutionsOf(instance.Id))))
        {
            foreach (var call in checks.Each(instance.Summary.Calls).Where(call => call.Kind is IrCallKind.Virtual or IrCallKind.Interface or IrCallKind.Delegate &&
                                                                                   call.Receivers.Count != 0 && call.Receivers.All(ReadsStorage)))
            {
                // A receiver the dispatch can run on — an object that may be of the call's target type — is one the analysis follows.
                var target = call.TargetContainingTypeKey is { } key ? ProgramIndex.Substitute(key, instance.Substitution) : null;
                if (call.Receivers.SelectMany(value => _heap.Resolve(instance.Id, value)).Any(region => MayBe(program, region, target)))
                    continue;
                var fillers = checks.Each(call.Receivers).SelectMany(value => observation.Fillers(instance, value)).Distinct(StringComparer.Ordinal).ToArray();
                var filled = UnfollowedFrom(fillers, unfollowed, edges);
                if (filled is null)
                    continue;
                _unfollowedReceivers.Add($"{call.Target} in {instance.BodyId}#{call.OperationId}, whose receiver only {string.Join(", ", fillers)} " +
                                         $"fills, through {filled}, which the analysis does not follow");
            }
        }

        foreach (var action in new[] { DriverSynthesizer.CALL, DriverSynthesizer.ENUMERATE })
        {
            checks.Check();
            _unseen[action] = UnseenIn(run, action);
        }
        HandedOnlyToUnsupportedOutsideSetup = _outsideSetup.Where(region => !_outsideSetupBeyondUnsupported.Contains(region)).ToHashSet(StringComparer.Ordinal);
        HandedOnlyToUnsupportedOutsideSetupDirectly = _outsideSetupDirectly.Where(region => !_outsideSetupBeyondUnsupportedDirectly.Contains(region))
                                                                           .ToHashSet(StringComparer.Ordinal);

        // What a call hands: all its regions reach, and of the regions it hands only to code a user may have written, what a user
        // can name.
        void AddCall((string Instance, int Operation) site, IEnumerable<string> regions, IEnumerable<string>? directly = null,
                     IEnumerable<string>? toUserCode = null)
        {
            var handed = regions.ToArray();
            var reached = directly?.ToArray() ?? handed;
            var closed = Close(calls.TryGetValue(site, out var existing) ? existing.Concat(handed) : handed);
            calls[site] = toUserCode is null ? closed : closed.Union(_reachability.VisibleFrom(toUserCode)).ToHashSet(StringComparer.Ordinal);
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

    /// <summary>The one decision on calls whose receiver can only be what a call the engine cannot follow put there: for each call an
    /// instance running outside setup dispatches on its receiver — a virtual or interface call, a delegate call; a call whose body is
    /// fixed runs without an object — whose receiver is read out of storage (<see cref="ReadsStorage"/>) and holds no object that may be
    /// of the call's target type (<see cref="MayBe"/>), while
    /// the bodies whose writes fill that storage (<see cref="ValueObservation.Fillers"/>) run or reach a call the analysis does not follow
    /// (<see cref="UnfollowedFrom"/>), as <c>Comparer&lt;T&gt;.Default</c> is filled by its type initializer through
    /// <c>ComparerHelpers.CreateDefaultComparer</c> and the extern behind <c>CreateInstanceForAnotherGenericParameter</c>: the call, the
    /// bodies and the call they reach, in words, sorted. Storage nothing writes holds its default, and a call on it only throws. Such a call runs
    /// code the analysis does not see, so the generator refuses the member (<see cref="ModelCauses.CALL_RECEIVER_UNFOLLOWED"/>) rather
    /// than give a model that leaves the call out; the engine's handling of the call in a user's analysis is not this decision's.
    /// Read by <c>EffectReader.ReadReasonsFromHandoffs</c>.</summary>
    public IReadOnlyCollection<string> UnfollowedReceiverCalls => _unfollowedReceivers;

    /// <summary>What the analysis did not see in the tree of <c>V_Call</c> or <c>V_Enum</c>, sorted: every unresolved dispatch of an
    /// instance running there, of any call kind, delegate calls included, and every dispatch without a receiver object in a body one
    /// runs; every reached body whose lowering was dropped, which has no
    /// instance to place in a tree and so counts in every tree; every unsupported operation of a body an instance running there has.
    /// Empty for any other action.</summary>
    /// <param name="action"><c>V_Call</c> or <c>V_Enum</c>.</param>
    public IReadOnlyList<string> Unseen(string action) => _unseen.GetValueOrDefault(action) ?? [];

    private IReadOnlyList<string> UnseenIn(ScopeRun run, string action)
    {
        var instances = _checks.Each(_heap.Instances.Values).Where(instance => ExecutionsOf(instance.Id).Any(execution => _executions.InTree(execution, action)))
                               .ToArray();
        var ids = instances.Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
        var unseen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (instance, operation) in _checks.Each(_heap.UnresolvedDispatches.Concat(_heap.UnresolvedCallTargets)).Where(site => ids.Contains(site.Instance)))
            unseen.Add($"unresolved dispatch: {instance}#{operation}");
        var bodies = instances.Select(instance => instance.BodyId).ToHashSet(StringComparer.Ordinal);
        foreach (var (body, operation) in _checks.Each(_heap.NoReceiverObjects).Where(site => bodies.Contains(site.BodyId)))
            unseen.Add($"no receiver object: {body}#{operation}");
        foreach (var dropped in _checks.Each(run.LoweringDiagnostics).Where(diagnostic => diagnostic.StartsWith(LOWERING, StringComparison.Ordinal)))
            unseen.Add(dropped);
        foreach (var body in _checks.Each(bodies))
        {
            if (run.Reachable.Bodies.TryGetValue(body, out var ir) &&
                _checks.Each(ir.Blocks.SelectMany(block => block.Operations)).Any(operation => operation is IrUnknownOperation { Reason: UNSUPPORTED }))
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

    /// <summary>The first call, by name, the analysis does not follow that a body filling storage runs or reaches through its calls —
    /// a call with no body, such as an extern —; null where none of the bodies reaches one.</summary>
    /// <param name="fillers">The bodies whose writes fill the storage.</param>
    /// <param name="unfollowed">The calls the analysis does not follow, by the instance making them.</param>
    /// <param name="edges">The call edges, by caller instance.</param>
    private string? UnfollowedFrom(IReadOnlyCollection<string> fillers, ILookup<string, string> unfollowed, ILookup<string, string> edges)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(_checks.Each(_heap.Instances.Values).Where(instance => fillers.Contains(instance.BodyId)).Select(instance => instance.Id));
        var found = new SortedSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var current))
        {
            _checks.Check();
            if (!seen.Add(current))
                continue;
            found.UnionWith(unfollowed[current]);
            foreach (var callee in _checks.Each(edges[current]))
                pending.Push(callee);
        }
        return found.Count == 0 ? null : found.Min;
    }

    /// <summary>Whether an object may be of a call's target type: always, but for one whose supertypes hold the target's definition
    /// only with other type arguments — a <c>GenericEqualityComparer&lt;string&gt;</c> is no <c>EqualityComparer&lt;T&gt;</c> of another
    /// <c>T</c>, which the dispatch, comparing definitions, lets through.</summary>
    /// <param name="program">The scope's types.</param>
    /// <param name="region">The object.</param>
    /// <param name="target">The call's target type, substituted; null where the call names none.</param>
    private bool MayBe(ProgramIndex program, string region, string? target)
    {
        if (target is null || !_heap.Regions.TryGetValue(region, out var held) || held.TypeKey is not { } type || program.Type(type) is null)
            return true;
        var definition = TypeSafety.DefinitionOf(target);
        var same = program.Supertypes(type).Append(type).Where(supertype => TypeSafety.DefinitionOf(supertype) == definition).ToArray();
        return same.Length == 0 || same.Contains(target, StringComparer.Ordinal);
    }

    /// <summary>Whether a receiver is read out of storage — a static field, a field or a cell along a path, a capture cell — rather than
    /// a value a call hands back directly: only state can be what a call with no body filled before the member ran.</summary>
    /// <param name="value">A receiver value.</param>
    private static bool ReadsStorage(AbstractValue value) => value is StaticFieldValue or PathValue or CapturedValue;

    /// <summary>Whether some of an instance's executions ran outside setup and are no artefact, as <see cref="File"/> files a site.</summary>
    /// <param name="executions">The instance's executions.</param>
    private bool OutsideSetup(IReadOnlySet<string> executions) =>
        !(executions.Count != 0 && executions.All(_executions.IsArtefact)) &&
        executions.Any(execution => !_executions.InSetup(execution) && !_executions.IsArtefact(execution));

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
        foreach (var ordinal in _checks.Each(call.Receivers).Select(ParameterBase).OfType<int>().Distinct())
        {
            var bound = _checks.Each(_heap.Edges).Any(edge => edge.CalleeInstance == instance.Id &&
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

    /// <summary>The objects a witness instance received, by where: what its receiver is and what each parameter got, together with what
    /// its witness stores read from either. A struct is no object (R2): a copy stands for the objects it holds, and a probe struct's
    /// witness stores the probe objects the struct holds (R9), which are what the member it stands for got. A class's witness stores
    /// what it got, which adds nothing.</summary>
    /// <param name="heap">The solved heap.</param>
    /// <param name="instance">A witness instance.</param>
    internal static (IReadOnlySet<string> Receivers, IReadOnlyDictionary<int, IReadOnlySet<string>> Parameters) Received(HeapSolution heap,
                                                                                                                           MethodInstance instance)
    {
        var receivers = Objects(instance.Receivers);
        var parameters = instance.Parameters.ToDictionary(pair => pair.Key, pair => Objects(pair.Value));
        foreach (var value in instance.Summary.Stores.Where(store => IsWitnessField(store.Field)).SelectMany(store => store.Values))
        {
            var target = ReceivedFrom(value) switch
            {
                Heap.ThisValue => receivers,
                ParameterValue parameter => parameters.TryGetValue(parameter.Ordinal, out var got) ? got : parameters[parameter.Ordinal] = new(StringComparer.Ordinal),
                _ => null
            };
            target?.UnionWith(Objects(heap.Resolve(instance.Id, value)));
        }

        return (receivers, parameters.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value));

        // A struct copy is no object: it stands for the objects its slots hold, through every copy inside it.
        HashSet<string> Objects(IEnumerable<string> regions)
        {
            var objects = new HashSet<string>(StringComparer.Ordinal);
            var copies = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(regions);
            while (pending.TryPop(out var region))
            {
                if (!heap.Regions.TryGetValue(region, out var held) || held.Kind != HeapRegionKind.StructCopy)
                    objects.Add(region);
                else if (copies.Add(region))
                {
                    foreach (var target in heap.FieldsOf(region).SelectMany(slot => heap.PointsTo(region, slot)))
                        pending.Push(target);
                }
            }

            return objects;
        }

        static AbstractValue ReceivedFrom(AbstractValue value) => value switch
        {
            PathValue path => ReceivedFrom(path.Base),
            StructPlaceValue place => ReceivedFrom(place.Base),
            _ => value
        };
    }

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
