using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The fate the generator classified for one parameter.</summary>
/// <param name="Fate"><c>invoke-now</c>, <c>iterator</c>, <c>holder</c> or <c>unknown-execution</c>.</param>
/// <param name="Holder">For <c>holder</c>, <c>result</c> or <c>this</c>; else <c>null</c>.</param>
public sealed record ClassifiedFate(string Fate, string? Holder);

/// <summary>The fates of one driver run, with what the classification observed.</summary>
/// <param name="Classified">The fate of each parameter the classifier gives one, by name.</param>
/// <param name="Refusals">By parameter, why the applicability rules refused the fate the probes showed.</param>
/// <param name="SetupWidened">The parameters with a probe setup fired or handed to a call the engine could not follow, sorted.</param>
public sealed record FateClassification(IReadOnlyDictionary<string, ClassifiedFate> Classified, IReadOnlyDictionary<string, string> Refusals,
                                        IReadOnlyList<string> SetupWidened);

/// <summary>Classifies where each delegate a member takes runs, by the open-world rule (SPEC TD-034b, G-5): a fate is one way the
/// delegate runs, and a delegate the engine saw run in two ways no single fate covers, or did not see run and is not kept only by a
/// proven holder, is <c>unknown-execution</c>. Kept is decided on the heap, a holder by reachability; triggers run only in a holder's
/// confirmation run (<see cref="Confirmed"/>).</summary>
public static class FateClassifier
{
    public const string INVOKE_NOW = "invoke-now";
    public const string ITERATOR = "iterator";
    public const string HOLDER = "holder";
    public const string UNKNOWN_EXECUTION = "unknown-execution";
    public const string RESULT = "result";
    public const string THIS = "this";

    /// <summary>The fates of a driver run that was not stopped.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The run.</param>
    public static FateClassification Classify(Driver driver, ScopeRun run)
    {
        if (run.Stopped)
            throw new ArgumentException("A stopped run has no heap to classify on.", nameof(run));
        return new Run(driver, run).Classify();
    }

    /// <summary>A parameter's fate after the applicability rules: the fate itself when they allow it, <c>unknown-execution</c> when
    /// they refuse it.</summary>
    /// <param name="member">The member's definition.</param>
    /// <param name="fate">The fate the probes showed.</param>
    /// <param name="refusal">Why the rules refused it, or <c>null</c>.</param>
    internal static ClassifiedFate Applied(IMethodSymbol member, ClassifiedFate fate, out string? refusal)
    {
        refusal = fate.Fate == UNKNOWN_EXECUTION ? null : Refusal(member, fate);
        return refusal is null ? fate : new ClassifiedFate(UNKNOWN_EXECUTION, null);
    }

    /// <summary>Why the applicability rules of TD-034a refuse a fate on a member, or <c>null</c> when they allow it: <c>holder</c>
    /// <c>this</c> needs an instance method that is not a constructor, <c>holder</c> <c>result</c> a constructor or a result of reference
    /// type, <c>iterator</c> a result that implements <c>System.Collections.IEnumerable</c>. The result is what the member returns, or
    /// <c>T</c> of a <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c> it returns, or the type a constructor constructs.</summary>
    /// <param name="member">The member's definition.</param>
    /// <param name="fate">The fate.</param>
    public static string? Refusal(IMethodSymbol member, ClassifiedFate fate)
    {
        var constructs = member.MethodKind == MethodKind.Constructor;
        var result = ResultType(member);
        return (fate.Fate, fate.Holder) switch
        {
            (HOLDER, THIS) when constructs || member.IsStatic => "holder this needs an instance method that is not a constructor",
            (HOLDER, RESULT) when !constructs && result is not { IsReferenceType: true } =>
                "holder result needs a constructor or a method whose result is of a reference type",
            (ITERATOR, _) when result is null || !IsSequence(result) => "iterator needs a method whose result implements System.Collections.IEnumerable",
            _ => null
        };
    }

    /// <summary>The member's result type: the type a constructor constructs, <c>T</c> of an awaited <c>Task&lt;T&gt;</c> or
    /// <c>ValueTask&lt;T&gt;</c>, the return type otherwise; <c>null</c> for <c>void</c>, <c>Task</c> and <c>ValueTask</c>.</summary>
    /// <param name="member">The member.</param>
    public static ITypeSymbol? ResultType(IMethodSymbol member)
    {
        if (member.MethodKind == MethodKind.Constructor)
            return member.ContainingType;
        if (member.ReturnsVoid)
            return null;
        return TypeShape.Of(member.ReturnType) switch
        {
            TypeShapeKind.Task => null,
            TypeShapeKind.TaskOfT => ((INamedTypeSymbol)member.ReturnType).TypeArguments[0],
            _ => member.ReturnType
        };
    }

    /// <summary>The fates after a holder's confirmation run (A5): a <c>holder</c> stands only when the driver's triggers cover that
    /// holder — every member a caller could invoke on its static type has a trigger (<see cref="Driver.Covers"/>) — and the run that
    /// roots the triggers beside the three actions stayed under the bound, finished, and every firing of a probe any trigger hands
    /// over for that parameter is in the root execution of a driver action other than setup (A3 applies); otherwise the parameter is
    /// <c>unknown-execution</c>. A member no trigger calls, a trigger that runs the delegate in a <c>Task.Run</c>, a timer or an
    /// unknown execution, and a holder with no trigger at all each fail it.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="classified">The fates of the fate run.</param>
    /// <param name="confirmation">The confirmation run, or <c>null</c> when it threw or did not run.</param>
    public static IReadOnlyDictionary<string, ClassifiedFate> Confirmed(Driver driver, IReadOnlyDictionary<string, ClassifiedFate> classified,
                                                                        ScopeRun? confirmation)
    {
        var fired = confirmation is { Stopped: false } ? FiredByField(confirmation) : null;
        var executions = fired is null ? null : new DriverExecutions(driver, confirmation!.Executions!, new HashSet<string>(StringComparer.Ordinal));
        var triggers = driver.Triggers.Select(trigger => trigger.Action).ToHashSet(StringComparer.Ordinal);
        bool Confirms(DriverParameter parameter, string holder) =>
            fired is not null && driver.Covers(holder) &&
            parameter.Probes.Where(probe => triggers.Contains(probe.Variant))
                     .SelectMany(probe => fired.GetValueOrDefault(probe.FiredField) ?? [])
                     .All(executions!.IsOwnOfAnAction);

        var confirmed = new SortedDictionary<string, ClassifiedFate>(StringComparer.Ordinal);
        foreach (var (name, fate) in classified)
        {
            confirmed[name] = fate.Fate == HOLDER && !Confirms(driver.Parameters.Single(parameter => parameter.Name == name), fate.Holder!)
                ? new ClassifiedFate(UNKNOWN_EXECUTION, null)
                : fate;
        }

        return confirmed;
    }

    /// <summary>By driver field, the executions a store into it ran in: where each probe, through its fired field, ran.</summary>
    /// <param name="run">A run that was not stopped.</param>
    private static Dictionary<string, HashSet<string>> FiredByField(ScopeRun run)
    {
        var fired = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var access in run.Executions!.Accesses.Where(access => access.Access is { Kind: SummaryAccessKind.Store, Field.IsStatic: true } &&
                                                                      access.Access.Field.Assembly == DriverSynthesizer.ASSEMBLY &&
                                                                      access.Access.Field.ContainingTypeId == DriverSynthesizer.DRIVER_TYPE))
        {
            if (!fired.TryGetValue(access.Access.Field.Name, out var executions))
                fired[access.Access.Field.Name] = executions = new HashSet<string>(StringComparer.Ordinal);
            executions.Add(access.ExecutionId);
        }

        return fired;
    }

    private static bool IsSequence(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_Collections_IEnumerable ||
        type.AllInterfaces.Any(@interface => @interface.SpecialType == SpecialType.System_Collections_IEnumerable);

    /// <summary>One run's classification.</summary>
    private sealed class Run
    {
        private static readonly string KeepResult = HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.KEEP_TYPE, "R");
        private static readonly string ReceiverOfCall = DriverSlot($"Recv_{DriverSynthesizer.CALL_VARIANT}");
        private static readonly string Inputs = DriverSlot("In_");
        private static readonly string Witnessed = HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.WITNESSED_TYPE, "W");
        private static readonly string WitnessedRefs = HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.WITNESSED_REF_TYPE, "Value");

        private readonly Driver _driver;
        private readonly HeapSolution _heap;
        private readonly HeapReachability _reach;
        private readonly DriverExecutions _executions;
        private readonly IReadOnlyList<PathStart> _starts;
        private readonly HashSet<string> _written = new(StringComparer.Ordinal);
        private readonly IReadOnlySet<string> _handed;
        private readonly IReadOnlySet<string> _handedBySetup;
        private readonly Dictionary<string, HashSet<string>> _fired;
        private readonly string? _resultObject;
        private readonly string? _receiverObject;
        private readonly bool _resultHanded;
        private readonly bool _resultUnknown;
        private readonly bool _enumerationLost;

        public Run(Driver driver, ScopeRun run)
        {
            _driver = driver;
            _heap = run.Heap!;
            _reach = new HeapReachability(_heap, Held(driver, run));
            _starts = _reach.StaticFields().Where(start => !start.Slot.StartsWith(Inputs, StringComparison.Ordinal) &&
                                                          !start.Slot.StartsWith(Witnessed, StringComparison.Ordinal) &&
                                                          !start.Slot.StartsWith(WitnessedRefs, StringComparison.Ordinal)).ToArray();
            var result = _reach.StaticField(KeepResult);
            var resultTargets = result is null ? new HashSet<string>(StringComparer.Ordinal) : _reach.Targets(result);
            _executions = new DriverExecutions(driver, run.Executions!, _reach.From(resultTargets));
            var allocations = new Allocations(driver, _heap, run.Executions!, _executions, _reach);

            var handoffs = new GenerationHandoffs(driver, run, _executions, _reach);
            _handed = handoffs.HandedInSetup.Concat(handoffs.HandedOutsideSetup)
                                .Concat(handoffs.WitnessedInSetup).Concat(handoffs.WitnessedOutsideSetup)
                                .ToHashSet(StringComparer.Ordinal);
            _handedBySetup = handoffs.HandedInSetup.Concat(handoffs.WitnessedInSetup).ToHashSet(StringComparer.Ordinal);

            // What an execution other than setup writes, and where each probe fired.
            foreach (var access in run.Executions!.Accesses.Where(access => !_executions.InSetup(access.ExecutionId)))
            {
                if (access.Access.Kind is SummaryAccessKind.Store or SummaryAccessKind.UnknownEffect)
                    _written.Add(access.RegionId);
            }

            foreach (var access in run.Collection!.Accesses.Where(access => !_executions.InSetup(access.ExecutionId) &&
                                                                          access.Operation is not (AccessOperation.Read or AccessOperation.AtomicRead)))
            {
                _written.UnionWith(_reach.WrittenObjects(access));
            }

            _fired = FiredByField(run);

            // The result object: the one object Keep.R points to, made by the member while V_Call ran. The receiver: Recv_Call's one object.
            _resultObject = resultTargets.Count == 1 &&
                            allocations.Of(resultTargets.First()) is { Kind: AllocationKind.MemberObject } or
                                                                     { Kind: AllocationKind.LibraryDuring, Action: DriverSynthesizer.CALL }
                ? resultTargets.First()
                : null;
            _receiverObject = _reach.StaticField(ReceiverOfCall) is { } receiver && _reach.Targets(receiver) is { Count: 1 } receivers ? receivers.First() : null;
            _resultHanded = _resultObject is not null && _handed.Contains(_resultObject);

            // The heap carries no value through an await: what the task completes with is read from an async member's returns only, and
            // V_Enum's foreach over an awaited result enumerates nothing.
            _resultUnknown = AwaitsValue(driver) && !driver.Member.IsAsync &&
                             TypeShape.Of(ResultType(driver.Member)!) is not (TypeShapeKind.Immutable or TypeShapeKind.PlainStruct);
            _enumerationLost = driver.Enumerates && AwaitsValue(driver);
        }

        public FateClassification Classify()
        {
            var classified = new SortedDictionary<string, ClassifiedFate>(StringComparer.Ordinal);
            var refusals = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var widened = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var parameter in _driver.Parameters)
            {
                var calls = parameter.Probes.Where(probe => probe.Variant == DriverSynthesizer.CALL_VARIANT).ToArray();
                var fates = calls.Select(probe => FateOf(parameter, probe)).Distinct().ToArray();
                classified[parameter.Name] = Applied(_driver.Member, fates is [var single] ? single : new ClassifiedFate(UNKNOWN_EXECUTION, null),
                                                     out var refusal);
                if (refusal is not null)
                    refusals[parameter.Name] = refusal;
                if (parameter.Probes.Any(probe => Fired(probe).Any(_executions.InSetup) || Regions(probe).Overlaps(_handedBySetup)))
                    widened.Add(parameter.Name);
            }

            return new FateClassification(classified, refusals, widened.ToArray());
        }

        /// <summary>The first fate of G-5 that applies to one probe of <c>V_Call</c>, with its counterpart of <c>V_Enum</c>. Each fate
        /// runs the delegate in the execution of the action that called the member; a counterpart the engine saw run anywhere else ran in
        /// a way no fate covers. A result the heap cannot say keeps everything, and a sequence V_Enum could not enumerate shows neither
        /// how it runs the delegate nor that it only holds it.</summary>
        /// <param name="parameter">The parameter.</param>
        /// <param name="probe">The probe of <c>V_Call</c>.</param>
        private ClassifiedFate FateOf(DriverParameter parameter, DriverProbe probe)
        {
            var counterpart = parameter.Probes.FirstOrDefault(other => other.Variant == DriverSynthesizer.ENUMERATE_VARIANT && other.Index == probe.Index);
            if (counterpart is not null && Fired(counterpart).Any(execution => !DriverExecutions.IsOwn(execution, DriverSynthesizer.ENUMERATE)))
                return new ClassifiedFate(UNKNOWN_EXECUTION, null);

            var fired = Fired(probe);
            var kept = Kept(parameter, probe, null) || Regions(probe).Overlaps(_handed);
            if (fired.Count != 0 && fired.All(execution => DriverExecutions.IsOwn(execution, DriverSynthesizer.CALL)) && !kept)
                return new ClassifiedFate(INVOKE_NOW, null);
            if (fired.Count != 0)
                return new ClassifiedFate(UNKNOWN_EXECUTION, null);

            var throughResult = !_enumerationLost && _resultObject is not null && OnlyThrough(parameter, probe, _resultObject, [KeepResult]);
            if (throughResult && counterpart is not null && Fired(counterpart) is { Count: > 0 } enumerated &&
                enumerated.All(execution => DriverExecutions.IsOwn(execution, DriverSynthesizer.ENUMERATE)))
            {
                return new ClassifiedFate(ITERATOR, null);
            }

            if (throughResult)
                return new ClassifiedFate(HOLDER, RESULT);
            if (_receiverObject is not null)
            {
                IReadOnlyList<string> own = _reach.StaticField(KeepResult) is { } result && _reach.Targets(result).Contains(_receiverObject)
                    ? [ReceiverOfCall, KeepResult]
                    : [ReceiverOfCall];
                if (OnlyThrough(parameter, probe, _receiverObject, own))
                    return new ClassifiedFate(HOLDER, THIS);
            }

            return new ClassifiedFate(UNKNOWN_EXECUTION, null);
        }

        /// <summary>Whether a counted path reaches the probe, avoiding <paramref name="avoid"/> when it is set: one from any path start
        /// but the parameter's own field, or one from its own field that passes an object an execution other than setup writes. When
        /// the heap cannot say what the result holds, the result may hold the probe, on any path.</summary>
        /// <param name="parameter">The parameter.</param>
        /// <param name="probe">The probe of <c>V_Call</c>.</param>
        /// <param name="avoid">An object every counted path must avoid, or <c>null</c>.</param>
        private bool Kept(DriverParameter parameter, DriverProbe probe, string? avoid)
        {
            if (_resultUnknown)
                return true;
            var targets = Regions(probe);
            var own = parameter.OwnFields.TryGetValue(probe.Variant, out var field) ? DriverSlot(field) : null;
            return _starts.Any(start => _reach.Reaches(start, targets, avoid, start.Slot != own, _written));
        }

        /// <summary>Whether the probe is kept only through an object: it is kept, every counted path passes the object, the object is
        /// reachable from no path start but its own driver fields, and it was handed to no call the engine could not follow.</summary>
        /// <param name="parameter">The parameter.</param>
        /// <param name="probe">The probe of <c>V_Call</c>.</param>
        /// <param name="holder">The object.</param>
        /// <param name="ownFields">The driver fields the object may be reached from.</param>
        private bool OnlyThrough(DriverParameter parameter, DriverProbe probe, string holder, IReadOnlyList<string> ownFields)
        {
            if (_handed.Contains(holder) || Regions(probe).Overlaps(_handed))
                return false;
            if (!Kept(parameter, probe, null) || Kept(parameter, probe, holder))
                return false;
            var target = new HashSet<string>(StringComparer.Ordinal) { holder };
            return !_starts.Where(start => !ownFields.Contains(start.Slot))
                           .Any(start => _reach.Reaches(start, target, null, counted: true, _written));
        }

        /// <summary>The executions a probe fired in, the escape artefact left out unless the result object was handed to a call the engine
        /// could not follow, which may have enumerated it itself, and the root execution of every other action but setup left out: a
        /// probe gets there only through a static, which keeps it for the kept rule, or through a library method instance the engine
        /// shares between the actions' calls.</summary>
        /// <param name="probe">The probe.</param>
        private IReadOnlySet<string> Fired(DriverProbe probe) =>
            (_fired.GetValueOrDefault(probe.FiredField) ?? []).Where(execution => (_resultHanded || !_executions.IsArtefact(execution)) &&
                                                                                  !_executions.IsOwnOfAnotherAction(execution, ActionOf(probe.Variant)))
                                                              .ToHashSet(StringComparer.Ordinal);

        /// <summary>What static fields hold beyond what the heap says: every value an execution's store into a static field stored; every
        /// value a callee's <c>ref</c> or <c>out</c> parameter may hold when it returns, for a static field passed by reference — the
        /// heap carries such a value back to a caller's local only, never into the field, and that is how the member reaches the driver's
        /// <c>Out_</c> fields; and for <c>Keep.R</c>, what an awaited call completes with.</summary>
        /// <param name="driver">The driver.</param>
        /// <param name="run">The run.</param>
        private static Dictionary<string, IReadOnlySet<string>> Held(Driver driver, ScopeRun run)
        {
            var heap = run.Heap!;
            var held = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            HashSet<string> At(string slot)
            {
                if (!held.TryGetValue(slot, out var values))
                    held[slot] = values = new HashSet<string>(StringComparer.Ordinal);
                return values;
            }

            static string SlotOf(IrFieldRef field) => HeapReachability.Slot(field.Assembly, field.ContainingTypeId, field.Name);

            foreach (var access in run.Executions!.Accesses.Where(access => access.Access is { Kind: SummaryAccessKind.Store, Field.IsStatic: true }))
                At(SlotOf(access.Access.Field)).UnionWith(access.Access.Values.SelectMany(value => heap.Resolve(access.InstanceId, value)));
            foreach (var edge in heap.Edges)
            {
                if (!heap.Instances.TryGetValue(edge.CallerInstance, out var caller) || !heap.Instances.TryGetValue(edge.CalleeInstance, out var callee))
                    continue;
                foreach (var argument in caller.Summary.Calls.Where(call => call.OperationId == edge.OperationId).SelectMany(call => call.Arguments))
                {
                    foreach (var cell in argument.References.OfType<ReferenceCell>().Where(cell => cell.Field.IsStatic))
                    {
                        At(SlotOf(cell.Field)).UnionWith(callee.Summary.RefParameters.Where(parameter => parameter.Ordinal == argument.ParameterOrdinal)
                                                       .SelectMany(parameter => parameter.Values)
                                                       .SelectMany(value => heap.Resolve(callee.Id, value)));
                    }
                }
            }

            At(KeepResult).UnionWith(AwaitedResult(driver, heap));
            return held.Select(pair => (pair.Key, Values: pair.Value.Where(heap.Regions.ContainsKey).ToHashSet(StringComparer.Ordinal)))
                       .Where(pair => pair.Values.Count != 0)
                       .ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Values, StringComparer.Ordinal);
        }

        /// <summary>What <c>Keep.R</c> holds beyond what the heap says when the driver awaits the member's <c>Task&lt;T&gt;</c> or
        /// <c>ValueTask&lt;T&gt;</c>: what the task completes with, which the heap does not carry through an <c>await</c> — the values
        /// the member's instances that <c>V_Call</c> calls return. Empty for a member the driver does not await for a value.</summary>
        /// <param name="driver">The driver.</param>
        /// <param name="heap">The run's heap.</param>
        private static IReadOnlySet<string> AwaitedResult(Driver driver, HeapSolution heap)
        {
            var awaited = new HashSet<string>(StringComparer.Ordinal);
            if (!AwaitsValue(driver))
                return awaited;
            var call = $"body:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.{DriverSynthesizer.CALL}";
            var member = IrLowering.RootBodyId(driver.Member);
            foreach (var edge in heap.Edges.Where(edge => heap.Instances.TryGetValue(edge.CallerInstance, out var caller) && caller.BodyId == call &&
                                                          heap.Instances.TryGetValue(edge.CalleeInstance, out var callee) && callee.BodyId == member))
            {
                var callee = heap.Instances[edge.CalleeInstance];
                awaited.UnionWith(callee.Summary.Returns.SelectMany(returned => returned.Values).SelectMany(value => heap.Resolve(callee.Id, value)));
            }

            return awaited;
        }

        /// <summary>Whether the driver awaits the member's call for a value: a method returning <c>Task&lt;T&gt;</c> or
        /// <c>ValueTask&lt;T&gt;</c>.</summary>
        /// <param name="driver">The driver.</param>
        private static bool AwaitsValue(Driver driver) =>
            driver.Member.MethodKind != MethodKind.Constructor && TypeShape.Of(driver.Member.ReturnType) == TypeShapeKind.TaskOfT;

        /// <summary>The action that hands over a variant's probes: <c>V_Call</c>, <c>V_Enum</c>, or the trigger action of the same
        /// name, which the fate run does not root.</summary>
        /// <param name="variant">The variant.</param>
        private static string ActionOf(string variant) => variant switch
        {
            DriverSynthesizer.CALL_VARIANT => DriverSynthesizer.CALL,
            DriverSynthesizer.ENUMERATE_VARIANT => DriverSynthesizer.ENUMERATE,
            _ => variant
        };

        /// <summary>The delegate regions that are a probe: those whose site names its factory.</summary>
        /// <param name="probe">The probe.</param>
        private HashSet<string> Regions(DriverProbe probe)
        {
            var body = Allocations.ProbeBody(probe);
            return _heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Delegate && region.SiteBodyId == body)
                        .Select(region => region.Identity).ToHashSet(StringComparer.Ordinal);
        }

        private static string DriverSlot(string field) => HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.DRIVER_TYPE, field);
    }
}
