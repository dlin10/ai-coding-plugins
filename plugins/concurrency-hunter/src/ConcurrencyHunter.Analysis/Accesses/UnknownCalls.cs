using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Accesses;

/// <summary>An unresolved call of one instance (R1): an opaque call no library model describes, a dispatch with no receiver
/// object, an operation on a <c>dynamic</c> value, or a locator nothing resolves. <see cref="Receivers"/> are seen only through the
/// state of <see cref="DeclaringTypeKey"/>; <see cref="Arguments"/> are seen whole. A locator makes a semantic gap and has no unknown
/// effect: the DI model keeps it (R1, R4).</summary>
/// <param name="Instance">The instance making the call.</param>
/// <param name="OperationId">The call's operation in that instance.</param>
/// <param name="Callee">The called member's display.</param>
/// <param name="Kind">The kind of unresolved work.</param>
/// <param name="Receivers">The objects the call is made on.</param>
/// <param name="DeclaringTypeKey">The type whose state a receiver exposes, when restricted.</param>
/// <param name="Arguments">The values handed to the call.</param>
/// <param name="Delegates">The delegate objects handed to it.</param>
public sealed record UnknownCall(MethodInstance Instance, int OperationId, string Callee, string Kind, IReadOnlySet<AbstractValue> Receivers,
                                 string? DeclaringTypeKey, IReadOnlyList<CallArgument> Arguments, IReadOnlyList<DelegateCreationValue> Delegates)
{
    /// <summary>Whether the summary receiver is body-local fresh.</summary>
    public bool ReceiverIsFresh { get; init; }

    /// <summary>The one parameter or receiver the summary receiver is.</summary>
    public HandedSlot? ReceiverHandedSlot { get; init; }

    public IrProvenance? Provenance { get; init; }
    public IReadOnlyList<SummaryPredicate> Conditions { get; init; } = [];
    public bool IsLocator => Kind == SemanticGapKinds.MODEL_GAP;

    /// <summary>The escaped library sequence whose unknown enumeration makes this call, an unresolved iterator delegate of it or of a
    /// sequence it enumerates; null for a call made where it stands (R3, R5).</summary>
    public string? EnumeratedSequence { get; init; }

    /// <summary>The work this call is when the engine starts it itself — a spawn's work, a timer's callback, a delegate handed to an
    /// unresolved call — and it resolves to a member without a body: the call is made in the execution running that work, not where
    /// <see cref="OperationId"/> stands; null for a call made where it stands (question 145).</summary>
    public UnknownWork? Work { get; init; }
}

/// <summary>Where an unresolved call that is work runs: the execution of a spawn's work or a timer's callback at its site, or the unknown
/// execution of the handed delegate <see cref="DelegateRegion"/>.</summary>
/// <param name="Kind">Where the work runs.</param>
/// <param name="DelegateRegion">The delegate region handed over, for a handoff; null otherwise.</param>
public sealed record UnknownWork(UnresolvedWorkKind Kind, string? DelegateRegion);

/// <summary>The unresolved calls of a scope and what their unknown effects reach (R1). A call a recognizer of phases 1-4 models is not
/// unresolved: a member of a type one owns or a framework slice, a collection member, a DI registration or scope call, or a <c>Map*</c>
/// whose delegate the roots provider made a root.</summary>
public static class UnknownCalls
{
    private const string REFLECTION_NAMESPACE = "System.Reflection.";
    private const string ACTIVATOR = "System.Activator.";

    /// <summary>Every unresolved call of every instance the heap reached, in instance and operation order.</summary>
    /// <param name="scope">The process scope whose calls are classified.</param>
    /// <param name="heap">The solved heap and its reached instances.</param>
    public static IReadOnlyList<UnknownCall> Of(ScopeProgram scope, HeapSolution heap)
    {
        var modelled = new Modelled(scope);
        var locators = heap.UnresolvedLocators.ToHashSet();
        var calls = new List<UnknownCall>();
        foreach (var instance in heap.Instances.Values.OrderBy(instance => instance.Id, StringComparer.Ordinal))
        {
            WorkCancellation.Check();
            var summary = instance.Summary;
            foreach (var call in summary.OpaqueCalls.Where(call => !call.IsKnown))
            {
                WorkCancellation.Check();
                var isLocator = call.ServiceCall is { Kind: not IrServiceCallKind.ScopeCreation } && locators.Contains((instance.BodyId, call.OperationId));
                // What no recognizer models of a call of a recognized type has the unknown effect of an unresolved call, and is no
                // semantic gap (question 144).
                if (!isLocator && call.UnmodelledPart() is { } part)
                {
                    calls.Add(new UnknownCall(instance, part.OperationId, part.Callee, SemanticGapKinds.RECOGNIZED,
                                              part.IsConstructor ? new HashSet<AbstractValue>() : part.Receivers, part.DeclaringTypeKey, part.Arguments,
                                              part.Delegates)
                    {
                        Provenance = part.Provenance,
                        ReceiverIsFresh = !part.IsConstructor && part.ReceiverIsFresh,
                        ReceiverHandedSlot = part.IsConstructor ? null : part.ReceiverHandedSlot,
                        Conditions = part.Conditions
                    });
                }
                if (!isLocator && modelled.Contains(instance.BodyId, call))
                    continue;
                // A call through an interface is the member of the table each object it decides implements it with, and stays unresolved
                // for the others alone (ADR 0010, amendment of the phase 5b third run).
                var receivers = call.Receivers;
                var projected = CollectionObjects.LibraryCalls(call, value => heap.Resolve(instance.Id, value),
                    region => CollectionObjects.KindOf(heap.Regions[region], scope.Program, scope.Summaries)).ToArray();
                foreach (var direct in projected.Where(direct => !direct.IsKnown))
                {
                    WorkCancellation.Check();
                    AddLibraryUnknown(direct);
                }
                if (projected.Length != 0)
                {
                    receivers = receivers.SelectMany(value => heap.Resolve(instance.Id, value)).Distinct(StringComparer.Ordinal)
                        .Where(region => !HasLibrary(call.Implementations, region, null))
                        .Select(region => (AbstractValue)new RegionValue(region)).ToHashSet();
                    if (receivers.Count == 0)
                        continue;
                }
                if (heap.IteratorMemberReceivers.TryGetValue((instance.Id, call.OperationId), out var iteratorReceivers))
                {
                    var regions = receivers.SelectMany(value => heap.Resolve(instance.Id, value)).Distinct(StringComparer.Ordinal).ToArray();
                    if (regions.Length != 0)
                    {
                        var other = regions.Except(iteratorReceivers, StringComparer.Ordinal).ToArray();
                        if (other.Length == 0)
                            continue;
                        receivers = other.Select(region => (AbstractValue)new RegionValue(region)).ToHashSet();
                    }
                }
                if (call.Implementations.Count != 0 && Undecided(scope, heap, instance, receivers, call.Implementations) is { } undecided)
                {
                    if (undecided.Count == 0)
                        continue;
                    receivers = undecided;
                }

                // A holder decides the call as those objects do: its member runs what it keeps, and the call stays unresolved for any
                // other object alone (R4). A grouping decides its `Key`: it is what the model says (R5).
                if ((heap.Holders.Count != 0 || call.IsGroupingKey) && !call.IsConstructor)
                {
                    var regions = receivers.SelectMany(value => heap.Resolve(instance.Id, value)).Distinct(StringComparer.Ordinal).ToArray();
                    var held = regions.Where(region => heap.Holders.Contains(region) ||
                                                       call.IsGroupingKey && heap.LibrarySequences.TryGetValue(region, out var grouping) && grouping.IsGrouping)
                                      .ToArray();
                    if (held.Length != 0)
                    {
                        if (held.Length == regions.Length)
                            continue;
                        receivers = regions.Except(held, StringComparer.Ordinal).Order(StringComparer.Ordinal)
                                           .Select(region => (AbstractValue)new RegionValue(region)).ToHashSet();
                    }
                }

                // A constructor sees nothing through the object it creates: that object holds only what its arguments give it yet.
                calls.Add(new UnknownCall(instance, call.OperationId, call.Callee, isLocator ? SemanticGapKinds.MODEL_GAP : KindOf(call.Callee),
                                          call.IsConstructor ? new HashSet<AbstractValue>() : receivers, call.DeclaringTypeKey, call.Arguments,
                                          call.Delegates)
                {
                    Provenance = call.Provenance,
                    ReceiverIsFresh = !call.IsConstructor && call.ReceiverIsFresh,
                    ReceiverHandedSlot = call.IsConstructor ? null : call.ReceiverHandedSlot,
                    Conditions = call.Conditions
                });
            }

            // A dispatch sees the receiver objects whose implementation has no body through the type declaring that implementation; one
            // with no receiver object at all sees only its arguments (R1).
            foreach (var call in summary.Calls.Where(call => heap.UnresolvedDispatches.Contains((instance.Id, call.OperationId))))
            {
                WorkCancellation.Check();
                foreach (var direct in CollectionObjects.LibraryCalls(call, value => heap.Resolve(instance.Id, value),
                    region => CollectionObjects.KindOf(heap.Regions[region], scope.Program, scope.Summaries, call.Target, instance.BodyId)).Where(direct => !direct.IsKnown))
                {
                    WorkCancellation.Check();
                    AddLibraryUnknown(direct);
                }
                var unresolved = heap.UnresolvedDispatchReceivers.GetValueOrDefault((instance.Id, call.OperationId)) ?? new HashSet<(string, string?)>();
                var receivers = unresolved.Where(receiver => !HasLibrary(call.Implementations, receiver.Region, call.Target)).ToHashSet();
                if (unresolved.Count != 0 && receivers.Count == 0)
                    continue;
                var byType = receivers.GroupBy(receiver => receiver.DeclaringTypeKey)
                                      .Select(group => (Receivers: (IReadOnlySet<AbstractValue>)group.Select(receiver => (AbstractValue)new RegionValue(receiver.Region))
                                                                                                        .ToHashSet(),
                                                        DeclaringTypeKey: group.Key))
                                      .DefaultIfEmpty((new HashSet<AbstractValue>(), null));
                calls.AddRange(byType.Select(group => new UnknownCall(instance, call.OperationId, call.Callee, SemanticGapKinds.UNRESOLVED_DISPATCH,
                                                                      group.Receivers, group.DeclaringTypeKey, call.Arguments, [])
                                                      {
                                                          Provenance = call.Provenance,
                                                          ReceiverIsFresh = call.ReceiverIsFresh,
                                                          ReceiverHandedSlot = call.ReceiverHandedSlot,
                                                          Conditions = call.Conditions
                                                      }));
            }

            bool HasLibrary(IReadOnlyList<IrImplementation> implementations, string region, string? method) =>
                DecidedByLibrary(implementations, CollectionObjects.KindOf(heap.Regions[region], scope.Program, scope.Summaries, method, instance.BodyId));

            void AddLibraryUnknown(SummaryOpaqueCall direct) => calls.Add(new UnknownCall(instance, direct.OperationId, direct.Callee,
                KindOf(direct.Callee), direct.Receivers, direct.DeclaringTypeKey, direct.Arguments, direct.Delegates)
            {
                Provenance = direct.Provenance,
                ReceiverIsFresh = direct.ReceiverIsFresh,
                ReceiverHandedSlot = direct.ReceiverHandedSlot,
                Conditions = direct.Conditions
            });
            // A factory of `GetOrAdd` or `AddOrUpdate` whose body the heap has not runs as a call of the delegate would: an unresolved
            // dispatch, which sees what the factories that ran no body are handed — the key, the overload's argument, and, for an update
            // factory alone, the value the dictionary holds (R11).
            foreach (var call in summary.OpaqueCalls.Where(call => InterproceduralAccesses.RunsFactories(call.Collection) &&
                                                                   heap.UnresolvedDispatches.Contains((instance.Id, call.OperationId))))
            {
                WorkCancellation.Check();
                var member = call.Collection!;
                var inputs = heap.UnresolvedFactoryInputs.GetValueOrDefault((instance.Id, call.OperationId)) ?? new HashSet<IrFactoryInput>();
                var handed = call.Arguments.Where(argument => !member.Factories.Contains(argument.ParameterOrdinal) &&
                                                              (argument.ParameterOrdinal == member.KeyArgument && inputs.Contains(IrFactoryInput.Key) ||
                                                               argument.ParameterOrdinal == member.FactoryArgument && inputs.Contains(IrFactoryInput.Argument)))
                                 .Select(argument => argument with { IsFresh = false, HandedSlot = null }).ToList();
                if (inputs.Contains(IrFactoryInput.Held))
                {
                    handed.Add(new CallArgument(-1, call.Receivers.Select(receiver => (AbstractValue)(receiver is PathValue { IsWildcard: true }
                                                                              ? receiver
                                                                              : receiver is PathValue path
                                                                                  ? new PathValue(path.Base, [.. path.Segments, PathValue.ELEMENT])
                                                                                  : new PathValue(receiver, [PathValue.ELEMENT])))
                                                             .ToHashSet()));
                }
                var receivers = heap.UnresolvedDispatchReceivers.GetValueOrDefault((instance.Id, call.OperationId)) ?? new HashSet<(string, string?)>();
                calls.AddRange(receivers.GroupBy(receiver => receiver.DeclaringTypeKey)
                                        .Select(group => (Receivers: (IReadOnlySet<AbstractValue>)group.Select(receiver => (AbstractValue)new RegionValue(receiver.Region))
                                                                                                          .ToHashSet(),
                                                          DeclaringTypeKey: group.Key))
                                        .DefaultIfEmpty((new HashSet<AbstractValue>(), null))
                                        .Select(group => new UnknownCall(instance, call.OperationId, call.Callee, SemanticGapKinds.UNRESOLVED_DISPATCH,
                                                                         group.Receivers, group.DeclaringTypeKey, handed, [])
                                        {
                                            Provenance = call.Provenance,
                                            Conditions = call.Conditions
                                        }));
            }

            // A delegate a known call runs by its model's fate, or a holder's member runs, with no body the heap has is an unresolved
            // dispatch at the call, as a call of it there would be, which sees what that delegate is handed (R3, R4).
            foreach (var call in summary.OpaqueCalls.Where(call => heap.UnresolvedFateInputs.ContainsKey((instance.Id, call.OperationId)) &&
                                                                   heap.UnresolvedDispatches.Contains((instance.Id, call.OperationId))))
            {
                WorkCancellation.Check();
                var inputs = heap.UnresolvedFateInputs.GetValueOrDefault((instance.Id, call.OperationId)) ?? new HashSet<string>();
                IReadOnlyList<CallArgument> handed = inputs.Count == 0
                    ? []
                    : [new CallArgument(-1, inputs.Select(region => (AbstractValue)new RegionValue(region)).ToHashSet())];
                var receivers = heap.UnresolvedDispatchReceivers.GetValueOrDefault((instance.Id, call.OperationId)) ?? new HashSet<(string, string?)>();
                calls.AddRange(receivers.GroupBy(receiver => receiver.DeclaringTypeKey)
                                        .Select(group => (Receivers: (IReadOnlySet<AbstractValue>)group.Select(receiver => (AbstractValue)new RegionValue(receiver.Region))
                                                                                                          .ToHashSet(),
                                                          DeclaringTypeKey: group.Key))
                                        .DefaultIfEmpty((new HashSet<AbstractValue>(), null))
                                        .Select(group => new UnknownCall(instance, call.OperationId, call.Callee, SemanticGapKinds.UNRESOLVED_DISPATCH,
                                                                         group.Receivers, group.DeclaringTypeKey, handed, [])
                                        {
                                            Provenance = call.Provenance,
                                            Conditions = call.Conditions
                                        }));
            }

            // A `dynamic` receiver is seen whole, like the arguments and the value assigned: all of them are the operation's operands.
            calls.AddRange(summary.DynamicOperations.Select(operation => new UnknownCall(instance, operation.OperationId, operation.Callee, SemanticGapKinds.DYNAMIC,
                                                                                            new HashSet<AbstractValue>(), null,
                                                                                            operation.Operands.Count != 0 ? operation.Operands : [new CallArgument(0, operation.Values)], [])
                                                        {
                                                            Provenance = operation.Provenance,
                                                            Conditions = operation.Conditions
                                                        }));
        }

        // Work the engine starts itself that resolves to a member without a body is an unresolved dispatch of that member, made in the
        // execution running the work: it sees its receivers through the type declaring the member and what it is handed whole (question 145).
        foreach (var work in heap.UnresolvedWork)
        {
            WorkCancellation.Check();
            if (!heap.Instances.TryGetValue(work.Site.Instance, out var starter))
                continue;
            IReadOnlyList<CallArgument> handed = work.Inputs.Count == 0
                ? []
                : [new CallArgument(-1, work.Inputs.Select(region => (AbstractValue)new RegionValue(region)).ToHashSet())];
            var provenance = work.Site.Kind switch
            {
                UnresolvedWorkKind.Spawn => starter.Summary.Spawns.FirstOrDefault(spawn => spawn.OperationId == work.Site.OperationId)?.Provenance,
                UnresolvedWorkKind.Timer => starter.Summary.Timers.FirstOrDefault(timer => timer.OperationId == work.Site.OperationId)?.Provenance,
                _ => starter.Summary.OpaqueCalls.FirstOrDefault(call => call.OperationId == work.Site.OperationId)?.Provenance ??
                     starter.Summary.Calls.FirstOrDefault(call => call.OperationId == work.Site.OperationId)?.Provenance
            };
            calls.AddRange(work.Receivers.GroupBy(receiver => receiver.DeclaringTypeKey)
                               .Select(group => (Receivers: (IReadOnlySet<AbstractValue>)group.Select(receiver => (AbstractValue)new RegionValue(receiver.Region))
                                                                                                 .ToHashSet(),
                                                 DeclaringTypeKey: group.Key))
                               .DefaultIfEmpty((new HashSet<AbstractValue>(), null))
                               .Select(group => new UnknownCall(starter, work.Site.OperationId, work.Site.Callee, SemanticGapKinds.UNRESOLVED_DISPATCH,
                                                                group.Receivers, group.DeclaringTypeKey, handed, [])
                               {
                                   Provenance = provenance,
                                   Work = new UnknownWork(work.Site.Kind, work.Site.DelegateRegion)
                               }));
        }

        // An escaped library sequence is enumerated by an unknown execution as well, and there each iterator delegate no body resolves,
        // of it or of a sequence it enumerates, is an unresolved dispatch as at every other enumeration (R3, R5).
        foreach (var escaped in heap.LibrarySequences.Values.Where(sequence => !sequence.IsGrouping && heap.UnknownIterators.Contains(sequence.RegionId))
                                    .OrderBy(sequence => sequence.RegionId, StringComparer.Ordinal))
        {
            WorkCancellation.Check();
            foreach (var sequence in EnumeratedSequences(heap, escaped.RegionId, new HashSet<string>(StringComparer.Ordinal)))
            {
                WorkCancellation.Check();
                if (!heap.Instances.TryGetValue(sequence.CreatorInstance, out var creator))
                    continue;
                var provenance = creator.Summary.OpaqueCalls.FirstOrDefault(call => call.OperationId == sequence.CreatorOperation)?.Provenance;
                foreach (var dispatch in sequence.Unresolved)
                {
                    WorkCancellation.Check();
                    IReadOnlyList<CallArgument> handed = dispatch.Inputs.Count == 0
                        ? []
                        : [new CallArgument(-1, dispatch.Inputs.Select(region => (AbstractValue)new RegionValue(region)).ToHashSet())];
                    calls.AddRange(dispatch.Receivers.GroupBy(receiver => receiver.DeclaringTypeKey)
                                           .Select(group => (Receivers: (IReadOnlySet<AbstractValue>)group.Select(receiver => (AbstractValue)new RegionValue(receiver.Region))
                                                                                                             .ToHashSet(),
                                                             DeclaringTypeKey: group.Key))
                                           .DefaultIfEmpty((new HashSet<AbstractValue>(), null))
                                           .Select(group => new UnknownCall(creator, sequence.CreatorOperation, dispatch.Callee, SemanticGapKinds.UNRESOLVED_DISPATCH,
                                                                            group.Receivers, group.DeclaringTypeKey, handed, [])
                                           {
                                               Provenance = provenance,
                                               EnumeratedSequence = escaped.RegionId
                                           }));
                }
            }
        }

        return calls.OrderBy(call => call.Instance.Id, StringComparer.Ordinal).ThenBy(call => call.OperationId).ToArray();
    }

    /// <summary>Whether a receiver object of an unresolved dispatch is decided by a library implementation of the call, as a member of the
    /// table on its kind of object: such a receiver is no unresolved one. The heap's marking of unseen writes asks it too
    /// (<see cref="UnseenWrites"/>), so the two agree on which receivers the dispatch's unknown effect sees.</summary>
    /// <param name="implementations">What the call is on each kind of receiver object without a body of its own.</param>
    /// <param name="kind">The receiver object's kind (<see cref="CollectionObjects.KindOf"/>).</param>
    internal static bool DecidedByLibrary(IReadOnlyList<IrImplementation> implementations, string? kind) =>
        implementations.Any(implementation => implementation.Library is not null && implementation.Kind == kind);

    /// <summary>A library sequence and every library sequence enumerating it enumerates, through its sources.</summary>
    /// <param name="heap">The solved heap.</param>
    /// <param name="region">The sequence region to enumerate.</param>
    /// <param name="visited">The regions already visited in this walk.</param>
    internal static IEnumerable<LibrarySequence> EnumeratedSequences(HeapSolution heap, string region, HashSet<string> visited)
    {
        WorkCancellation.Check();
        if (!visited.Add(region) || !heap.LibrarySequences.TryGetValue(region, out var sequence) || sequence.IsGrouping)
            yield break;
        yield return sequence;
        foreach (var source in sequence.Sources)
        {
            WorkCancellation.Check();
            foreach (var enumerated in EnumeratedSequences(heap, source, visited))
            {
                WorkCancellation.Check();
                yield return enumerated;
            }
        }
    }

    /// <summary>The opaque calls a recognizer of phases 1-4 models, which are no unresolved calls (R1): a member of a type one owns or a
    /// framework slice, a collection member, a DI registration or scope call, or a <c>Map*</c> whose delegate the roots provider made a
    /// root. The heap asks it too, so that only an unresolved call runs what it is handed in an unknown execution (R3).</summary>
    public sealed class Modelled(ScopeProgram scope)
    {
        private readonly Dictionary<string, string> _memberOf =
            scope.Program.Methods.SelectMany(method => method.NestedBodyIds.Select(nested => (Nested: nested, method.MethodId)))
                 .GroupBy(pair => pair.Nested, StringComparer.Ordinal)
                 .ToDictionary(group => group.Key, group => group.First().MethodId, StringComparer.Ordinal);
        private readonly HashSet<(string, int)> _registrations =
            scope.DiIndex.Registrations.Where(registration => registration is { BodyId: not null, OperationId: not null })
                 .Select(registration => (registration.BodyId!, registration.OperationId!.Value))
                 .ToHashSet();
        // Only the mapping of a minimal API endpoint hands its delegate to the recognizer that made it a root; the same body handed to
        // any other call is handed to that call (R1, R3).
        private readonly HashSet<string> _minimalApiBodies =
            scope.Roots.Where(root => root.RootKind == MINIMAL_API).Select(root => root.Entry.BodyKey).ToHashSet(StringComparer.Ordinal);

        public bool Contains(string bodyId, SummaryOpaqueCall call)
        {
            WorkCancellation.Check();
            return call.IsRecognized || call.Collection is not null || call.ServiceCall is not null ||
                _registrations.Contains((_memberOf.GetValueOrDefault(bodyId) ?? bodyId, call.OperationId)) ||
                IsMapping(call.Callee) && call.Delegates.Any(created => _minimalApiBodies.Contains(created.Target));
        }

        /// <summary>Whether a callee is a <c>Map*</c> member, such as <c>EndpointRouteBuilderExtensions.MapGet(…)</c>.</summary>
        private static bool IsMapping(string callee)
        {
            var member = callee[..(callee.IndexOf('(') is var open and >= 0 ? open : callee.Length)];
            return member[(member.LastIndexOf('.') + 1)..].StartsWith(MAPPING_PREFIX, StringComparison.Ordinal);
        }
    }

    /// <summary>The receiver objects an interface call does not decide, as regions; null where it decides none of them, or the heap knows
    /// no receiver object at all, which leaves the call as undecided as any other.</summary>
    private static IReadOnlySet<AbstractValue>? Undecided(ScopeProgram scope, HeapSolution heap, MethodInstance instance, IReadOnlySet<AbstractValue> receivers,
                                                         IReadOnlyList<IrImplementation> implementations)
    {
        var regions = receivers.SelectMany(value => heap.Resolve(instance.Id, value)).Distinct(StringComparer.Ordinal).ToArray();
        var undecided = regions.Where(region => CollectionObjects.Decision(implementations, CollectionObjects.KindOf(heap.Regions[region], scope.Program,
                                                                                                                  scope.Summaries)) is null)
                               .Order(StringComparer.Ordinal)
                               .ToArray();
        return undecided.Length == regions.Length ? null : undecided.Select(region => (AbstractValue)new RegionValue(region)).ToHashSet();
    }

    private const string MINIMAL_API = "minimal-api";
    private const string MAPPING_PREFIX = "Map";

    /// <summary>The semantic gap kind of an opaque call by its callee: reflection, or an unknown library member.</summary>
    /// <param name="callee">The callee.</param>
    internal static string KindOf(string callee) =>
        callee.StartsWith(REFLECTION_NAMESPACE, StringComparison.Ordinal) || callee.StartsWith(ACTIVATOR, StringComparison.Ordinal)
            ? SemanticGapKinds.REFLECTION
            : SemanticGapKinds.UNKNOWN_LIBRARY;

    /// <summary>
    /// The reach of an unknown effect (R1). Through an argument it sees everything the argument reaches; an array the call creates for
    /// its arguments is judged by its elements. Through a receiver it sees only the state of the type declaring the member, so a library
    /// member called on an object of the run's own sees none of its fields. A library object's own state is no resource: through one the
    /// effect reaches only what the heap knows it holds, the structure and cells of a collection ADR 0010 models, the objects its fields
    /// point to and the values its members were handed, never a region of a type from metadata. An immutable object touches nothing, and
    /// a delegate reaches what it captures.
    /// </summary>
    /// <param name="scope">The scope whose program declares the types of the run's own.</param>
    /// <param name="heap">The solved heap whose regions and slots the effect reaches.</param>
    public sealed class Reach(ScopeProgram scope, HeapSolution heap)
    {
        private readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<IrFieldRef>>> _libraryFields = new(() => LibraryFields(scope, heap));

        /// <summary>The fields of an object an unknown effect may see: those its type of the run's own declares, and the fields of types
        /// from metadata the program names on it (R1). A restricted receiver is seen through its recorded declaring type,
        /// including when a DI region is labelled with the service interface.</summary>
        /// <param name="regionId">The receiver or argument region.</param>
        /// <param name="declaringTypeKey">The declaring type through which a receiver is seen, or null for an unrestricted object.</param>
        public IReadOnlyList<IrFieldRef> FieldsOf(string regionId, string? declaringTypeKey = null)
        {
            WorkCancellation.Check();
            return UnseenWrites.FieldsOf(scope.Program, heap.Regions[regionId].TypeKey, LibraryFieldsOf(regionId), declaringTypeKey) ?? [];
        }

        /// <summary>The values an unknown effect starts from: the receivers it may see, and every argument, an array created for the
        /// call by its elements. Through a receiver of a member a type of the run's own declares, only that type's state and its bases'
        /// is seen: <see cref="Start.DeclaringTypeKey"/> names it.</summary>
        /// <param name="call">The unresolved call whose unknown effect starts there.</param>
        public IEnumerable<Start> Starts(UnknownCall call)
        {
            WorkCancellation.Check();
            return Starts(call, null);
        }

        /// <summary>The starts of an access-level unknown effect, with the scope's handed-down freshness proof where supplied.</summary>
        /// <param name="call">The unresolved call.</param>
        /// <param name="freshness">The access collector's proof, or null to keep body-local argument freshness.</param>
        internal IEnumerable<Start> Starts(UnknownCall call, HandedFreshness? freshness)
        {
            var receiverIsFresh = freshness is not null &&
                                  (call.ReceiverIsFresh || freshness.IsFresh(call.Instance.Id, call.ReceiverHandedSlot));
            var declaring = call.DeclaringTypeKey is { } key && scope.Program.Type(key) is { IsSource: true } ? key : null;
            if (declaring is not null)
            {
                if (call.Receivers.Count != 0)
                    yield return new Start(call.Receivers, null, declaring) { IsFresh = receiverIsFresh };
            }
            else
            {
                // A library member sees an object of the run's own only through the state of the library type declaring it, and a
                // library object whole, as far as the heap knows it.
                var ownObjects = call.Receivers.Where(value => heap.Resolve(call.Instance.Id, value).Any(IsSourceObject)).ToHashSet();
                var libraryObjects = call.Receivers.Except(ownObjects).ToHashSet();
                if (libraryObjects.Count != 0)
                    yield return new Start(libraryObjects, null, null) { IsFresh = receiverIsFresh };
                if (ownObjects.Count != 0 && call.DeclaringTypeKey is { } library)
                    yield return new Start(ownObjects, null, library) { IsFresh = receiverIsFresh };
            }

            foreach (var argument in call.Arguments)
            {
                WorkCancellation.Check();
                yield return argument.CreatedElements is { } elements
                    ? new Start(elements.SelectMany(element => element.Values).ToHashSet(), null, null)
                    : new Start(argument.Values, argument.Collection, null)
                    {
                        IsFresh = argument.IsFresh || freshness?.IsFresh(call.Instance.Id, argument.HandedSlot) == true
                    };
            }
            foreach (var created in call.Delegates)
            {
                WorkCancellation.Check();
                yield return new Start(new HashSet<AbstractValue> { created }, null, null);
            }
        }

        /// <summary>The delegate regions the call is handed, directly or in an array or a collection it is handed: each runs in an
        /// unknown execution of its own (R3).</summary>
        public IEnumerable<string> Delegates(UnknownCall call)
        {
            var handed = Starts(call).SelectMany(start => start.Values).SelectMany(value => heap.Resolve(call.Instance.Id, value))
                                     .ToHashSet(StringComparer.Ordinal);
            return handed.Concat(handed.Where(region => InterproceduralAccesses.IsCollectionType(scope.Program, heap.Regions[region].TypeKey))
                                       .SelectMany(collection => heap.PointsTo(collection, PathValue.ELEMENT)
                                                                     .Concat(heap.PointsTo(collection, PathValue.KEYS))))
                         .Where(region => heap.Regions[region].Kind == HeapRegionKind.Delegate)
                         .Distinct(StringComparer.Ordinal);
        }

        /// <summary>The mutable regions the call's unknown effect and delegates reach, and whether it is handed a delegate.</summary>
        /// <param name="call">The unknown call whose reach is computed.</param>
        public (IReadOnlySet<string> Regions, bool TakesDelegate) Of(UnknownCall call)
        {
            var (regions, takesDelegate) = BitsOf(call);
            return (Bits.Numbers(regions).Select(RegionId).ToHashSet(StringComparer.Ordinal), takesDelegate);
        }

        /// <summary>What <see cref="Of"/> answers, the regions as bits over <see cref="Number"/>. The same receivers and arguments start
        /// the unknown effects of many calls, so each start's reach is worked out once and kept, and a call joins its starts' (#462). The
        /// one walk of a call's starts in turn reads alike, but for a receiver seen through its declaring type that its own walk meets
        /// again: that walk stops there, and the receiver stays unseen whole for a later start of the call too. Such a call is walked
        /// whole, its starts in turn (<see cref="Walk"/>), once for the same starts in the same order.</summary>
        /// <param name="call">The unknown call whose reach is computed.</param>
        internal (ulong[] Regions, bool TakesDelegate) BitsOf(UnknownCall call)
        {
            var starts = new List<(int Region, string? DeclaringTypeKey)>();
            foreach (var start in Starts(call))
            {
                WorkCancellation.Check();
                foreach (var region in start.Values.SelectMany(value => heap.Resolve(call.Instance.Id, value)).Distinct(StringComparer.Ordinal))
                {
                    WorkCancellation.Check();
                    starts.Add((Number(region), start.DeclaringTypeKey));
                }
            }

            _calls++;
            var reached = Bits.EMPTY;
            var metItself = false;
            foreach (var start in starts)
            {
                WorkCancellation.Check();
                var walk = WalkOf(start);
                metItself |= walk.MetItself;
                Bits.UnionWith(ref reached, walk.Reached);
            }
            if (metItself && starts.Count > 1)
            {
                _wholeWalks++;
                var key = string.Join(' ', starts.Select(start => $"{start.Region}:{start.DeclaringTypeKey}"));
                if (!_callWalks.TryGetValue(key, out var whole))
                    _callWalks.Add(key, whole = Walk(starts));
                reached = whole.Reached;
            }

            return (reached, Delegates(call).Any());
        }

        /// <summary>The number of a region in this reach's numbering, given the first time it is asked for.</summary>
        /// <param name="regionId">The region.</param>
        internal int Number(string regionId)
        {
            if (!_numbers.TryGetValue(regionId, out var number))
            {
                _numbers.Add(regionId, number = _regionIds.Count);
                _regionIds.Add(regionId);
            }
            return number;
        }

        /// <summary>The region a number stands for.</summary>
        /// <param name="number">The number (<see cref="Number"/>).</param>
        internal string RegionId(int number) => _regionIds[number];

        /// <summary>How many regions are numbered so far: a number is below it.</summary>
        internal int Numbered => _regionIds.Count;

        /// <summary>The regions an effect from one start reaches, as <see cref="BitsOf"/> joins them for a call.</summary>
        /// <param name="regionId">The start's region.</param>
        /// <param name="declaringTypeKey">The type a receiver is seen through, or null for a region seen whole.</param>
        internal IReadOnlySet<string> OfStart(string regionId, string? declaringTypeKey) =>
            Bits.Numbers(WalkOf((Number(regionId), declaringTypeKey)).Reached).Select(RegionId).ToHashSet(StringComparer.Ordinal);

        /// <summary>What the reach did, for a diagnostic run, over its whole life: the calls asked, the distinct starts answered, the calls
        /// walked whole, the regions numbered, the region visits all walks made, the distinct answers of starts and whole calls, the
        /// strongly connected components worked out, the slots' targets the walks followed, the visits of those walks of receivers seen
        /// through their declaring type that went region by region (<see cref="RestrictedWalk"/>), and the words of every distinct set
        /// of regions it keeps now; then the sets of components added to another, the sets passed over because a region with state of
        /// their component showed them held already, and the words the added ones went through (<see cref="TakeReach"/>).</summary>
        internal string Counts() =>
            $"calls={_calls} startWalks={_walks.Count} wholeCallWalks={_wholeWalks} regions={_regionIds.Count} visits={_visits} " +
            $"distinctReaches={_answers.Count} components={_components} edges={_edges} restrictedVisits={_restrictedVisits} " +
            $"reachWords={_reaches.Keys.Sum(bits => (long)bits.Length)} unions={_unions} skippedUnions={_skippedUnions} unionWords={_unionWords}";

        /// <summary>The reach of one start, worked out the first time it is asked for: a region seen whole reaches what its strongly
        /// connected component reaches, and a receiver seen through its declaring type is walked from the slots that type shows.</summary>
        /// <param name="start">The start region and the type restricting it, if it is a receiver seen through its declaring type.</param>
        private Walked WalkOf((int Region, string? DeclaringTypeKey) start)
        {
            if (!_walks.TryGetValue(start, out var walked))
            {
                walked = start.DeclaringTypeKey is { } declaringTypeKey
                    ? RestrictedWalk(start.Region, declaringTypeKey)
                    : new Walked(ComponentReach(start.Region), false);
                _walks.Add(start, walked);
                _answers.Add(walked.Reached);
            }
            return walked;
        }

        /// <summary>What an effect from a region seen whole reaches: the same for every region of its strongly connected component, and
        /// worked out for the component the first time a walk meets one of them (<see cref="Strong"/>).</summary>
        /// <param name="region">The region's number.</param>
        private ulong[] ComponentReach(int region)
        {
            if (ComponentOf(region) == UNSEEN)
                Strong(region);
            return _componentReaches[ComponentOf(region)];
        }

        /// <summary>
        /// Walks every region an effect from <paramref name="root"/> reaches that no earlier walk of components met, and gives each of
        /// their strongly connected components (Tarjan's) what it reaches: its own regions with state, and what the components its
        /// regions point to reach. A component is complete before any component pointing to it, so what it reaches is made of finished
        /// sets, once. A region's successors are taken last first, as a walk with a stack pops them: regions are met, and their targets
        /// numbered, in the order such a walk from the same root meets the regions it has not walked before.
        /// </summary>
        /// <param name="root">The region's number.</param>
        private void Strong(int root)
        {
            var frames = new Stack<Frame>();
            frames.Push(Enter(root));
            while (frames.TryPop(out var frame))
            {
                WorkCancellation.Check();
                if (Advance(ref frame, out var successor))
                {
                    frames.Push(frame);
                    _edges++;
                    var state = ComponentOf(successor);
                    if (state == UNSEEN)
                        frames.Push(Enter(successor));
                    else if (state == OPEN)
                        _low[frame.Region] = Math.Min(_low[frame.Region], _order[successor]);
                    continue;
                }

                if (frames.TryPeek(out var caller))
                    _low[caller.Region] = Math.Min(_low[caller.Region], _low[frame.Region]);
                if (_low[frame.Region] == _order[frame.Region])
                    Complete(frame.Region);
            }
        }

        /// <summary>Meets a region in a walk of components: asks its step, which numbers its targets, and opens it.</summary>
        /// <param name="region">The region's number.</param>
        private Frame Enter(int region)
        {
            _visits++;
            var step = StepOf(region);
            while (_componentOf.Count <= region)
            {
                WorkCancellation.Check();
                _componentOf.Add(UNSEEN);
                _order.Add(0);
                _low.Add(0);
            }
            _componentOf[region] = OPEN;
            _order[region] = _low[region] = _opened++;
            _open.Push(region);
            return step.Captures is { } captures
                ? new Frame { Region = region, Slot = 0, Target = captures.Length }
                : new Frame { Region = region, Slot = step.Slots.Length, Target = 0 };
        }

        /// <summary>Takes a region's next successor, last first: what a delegate captures, or the targets of its slots; an immutable
        /// object has none.</summary>
        /// <param name="frame">The region and how far its successors are taken, moved on.</param>
        /// <param name="successor">The successor taken, or -1 when none is left.</param>
        private bool Advance(ref Frame frame, out int successor)
        {
            var step = _steps[frame.Region]!;
            if (step.Captures is { } captures)
            {
                successor = frame.Target > 0 ? captures[--frame.Target] : -1;
                return successor >= 0;
            }
            while (frame.Target == 0)
            {
                WorkCancellation.Check();
                if (frame.Slot == 0)
                {
                    successor = -1;
                    return false;
                }
                frame.Slot--;
                frame.Target = step.Slots[frame.Slot].Targets.Length;
            }
            successor = step.Slots[frame.Slot].Targets[--frame.Target];
            return true;
        }

        /// <summary>Closes the strongly connected component whose first region is <paramref name="root"/>: its regions leave the open
        /// stack, and it reaches its regions with state and what the components they point to reach, those being complete.</summary>
        /// <param name="root">The number of the component's first region.</param>
        private void Complete(int root)
        {
            var component = _componentReaches.Count;
            var members = new List<int>();
            int member;
            do
            {
                WorkCancellation.Check();
                member = _open.Pop();
                _componentOf[member] = component;
                members.Add(member);
            }
            while (member != root);

            // The components it points to are taken by the one that keeps their array: components that reach the same keep one.
            var reached = Bits.EMPTY;
            var marked = -1;
            var below = new List<int>();
            foreach (var region in members)
            {
                WorkCancellation.Check();
                var step = _steps[region]!;
                if (!step.IsImmutable && step.Captures is null && (step.IsCollection || step.HasState))
                {
                    Bits.Add(ref reached, region);
                    if (marked < 0)
                        marked = region;
                }
                foreach (var successor in step.Captures ?? step.Slots.SelectMany(slot => slot.Targets))
                {
                    WorkCancellation.Check();
                    var other = _componentOf[successor];
                    if (other == component)
                        continue;
                    other = _keepers[other];
                    if (_stamps[other] != component)
                    {
                        _stamps[other] = component;
                        below.Add(other);
                    }
                }
            }

            // A component with no state of its own that points to one other component reaches what that one does, in the same array.
            if (below.Count == 1 && marked < 0)
                reached = _componentReaches[below[0]];
            else
            {
                // A component completes after every component it reaches, so one with a higher number may reach one with a lower,
                // never the reverse: taken highest first, a set covers the components it reaches before they come, and they are passed
                // over (Nuutila). What is reached is the same in any order.
                below.Sort((first, second) => second.CompareTo(first));
                foreach (var other in below)
                {
                    WorkCancellation.Check();
                    TakeReach(ref reached, other);
                }
                reached = Keep(reached);
            }

            _componentReaches.Add(reached);
            _stamps.Add(-1);
            _firstWords.Add(FirstWord(reached));
            if (_keeperOf.TryGetValue(reached, out var keeper))
            {
                // A region with state of a component reaching the same regions shows that set covered as well as the keeper's does.
                if (marked >= 0 && _representatives[keeper] < 0)
                    _representatives[keeper] = marked;
            }
            else
                _keeperOf.Add(reached, keeper = component);
            _keepers.Add(keeper);
            _representatives.Add(marked);
            _components++;
        }

        /// <summary>
        /// Adds what a complete component reaches to a set being made of the sets of complete components, unless the set holds it
        /// already: the component reaches nothing, or the set holds a region with state of the component — a region that only a set of a
        /// component reaching it holds, and that set holds everything the component reaches. The words before the first that holds a
        /// region are not read.
        /// </summary>
        /// <param name="reached">The set being made, changed in place.</param>
        /// <param name="component">The number of the component that keeps the array to add (<see cref="_keepers"/>).</param>
        private void TakeReach(ref ulong[] reached, int component)
        {
            var reach = _componentReaches[component];
            var first = _firstWords[component];
            if (first >= reach.Length)
                return;
            if (_representatives[component] is >= 0 and var shown && Bits.Contains(reached, shown))
            {
                _skippedUnions++;
                return;
            }
            Bits.UnionWith(ref reached, reach, first);
            _unions++;
            _unionWords += reach.Length - first;
        }

        /// <summary>The first word of a set that holds a number, or its length when none does.</summary>
        /// <param name="set">The set.</param>
        private static int FirstWord(ulong[] set)
        {
            var first = 0;
            // bounded: one pass over the set's words, at most the reach's numbering over 64.
            while (first < set.Length && set[first] == 0)
                first++;
            return first;
        }

        /// <summary>
        /// Lets go of what only the walks need — the components, what each reaches, the walk of components and the steps of the regions —
        /// and keeps the numbering, the answers of the starts and of the calls walked whole and the counters. A later search for gaps over
        /// the same calls asks only for those answers (<see cref="SemanticGaps.Find"/>), so it walks nothing. A start asked later is walked
        /// from the heap again, as the first time.
        /// </summary>
        internal void ReleaseWalks()
        {
            WorkCancellation.Check();
            _steps.Clear();
            _steps.TrimExcess();
            _componentOf.Clear();
            _componentOf.TrimExcess();
            _order.Clear();
            _order.TrimExcess();
            _low.Clear();
            _low.TrimExcess();
            _open.Clear();
            _open.TrimExcess();
            _opened = 0;
            _componentReaches.Clear();
            _componentReaches.TrimExcess();
            _stamps.Clear();
            _stamps.TrimExcess();
            _firstWords.Clear();
            _firstWords.TrimExcess();
            _keepers.Clear();
            _keepers.TrimExcess();
            _representatives.Clear();
            _representatives.TrimExcess();
            _keeperOf.Clear();
            _keeperOf.TrimExcess();
            // The answers stay the one copy of their sets, so an equal set a later walk reaches is the same answer.
            _reaches.Clear();
            _reaches.TrimExcess();
            foreach (var answer in _answers)
            {
                WorkCancellation.Check();
                _reaches.Add(answer, answer);
            }
        }

        /// <summary>
        /// The walk of a receiver seen through its declaring type: the receiver with what that type shows of it, then what is reached from
        /// the slots it shows, the receiver itself never again, so it stays unseen whole in this walk (R1). Of the regions a walk of
        /// components has met, only those of the receiver's own component can lead back to it: one of another component reaches here what
        /// its component reaches. So this walk goes region by region only through the receiver's component, when a walk of components has
        /// met it, or else through regions no such walk has met, in the order a walk with a stack meets them, and where the receiver is
        /// not met again, a walk of components then meets the regions it went through.
        /// </summary>
        /// <param name="start">The receiver's number.</param>
        /// <param name="declaringTypeKey">The type the receiver is seen through.</param>
        private Walked RestrictedWalk(int start, string declaringTypeKey)
        {
            _visits++;
            _restrictedVisits++;
            var step = StepOf(start);
            if (step.IsImmutable)
                return new Walked(Keep(Bits.EMPTY), false);
            var reached = Bits.EMPTY;
            int[] targets;
            if (step.Captures is { } captures)
                targets = captures;
            else
            {
                var restricted = Seen(FieldsOf(RegionId(start), declaringTypeKey), declaringTypeKey).Select(FieldSlot.Key).ToHashSet(StringComparer.Ordinal);
                if (step.IsCollection || restricted.Count > 0)
                    Bits.Add(ref reached, start);
                targets = step.Slots.Where(slot => restricted.Contains(slot.Root) || slot.Root == PathValue.KEPT ||
                                                   step.IsCollection && PathValue.IsStorage(slot.Root))
                              .SelectMany(slot => slot.Targets)
                              .ToArray();
            }

            var startComponent = ComponentOf(start);
            var visited = Bits.EMPTY;
            var metItself = false;
            var pending = new Stack<int>(targets);
            while (pending.TryPop(out var region))
            {
                WorkCancellation.Check();
                if (region == start)
                {
                    metItself = true;
                    continue;
                }
                if (!Bits.Add(ref visited, region))
                    continue;
                if (ComponentOf(region) is >= 0 and var component && component != startComponent)
                {
                    TakeReach(ref reached, _keepers[component]);
                    continue;
                }

                _visits++;
                _restrictedVisits++;
                var next = StepOf(region);
                if (next.IsImmutable)
                    continue;
                if (next.Captures is null && (next.IsCollection || next.HasState))
                    Bits.Add(ref reached, region);
                foreach (var target in next.Captures ?? next.Slots.SelectMany(slot => slot.Targets))
                {
                    WorkCancellation.Check();
                    _edges++;
                    pending.Push(target);
                }
            }

            if (!metItself && startComponent == UNSEEN)
            {
                for (var at = targets.Length - 1; at >= 0; at--)
                {
                    WorkCancellation.Check();
                    if (ComponentOf(targets[at]) == UNSEEN)
                        Strong(targets[at]);
                }
            }
            return new Walked(Keep(reached), metItself);
        }

        /// <summary>The component of a region: <see cref="UNSEEN"/> before a walk of components meets it, <see cref="OPEN"/> while that
        /// walk is in it, and the component's number once it is complete.</summary>
        /// <param name="region">The region's number.</param>
        private int ComponentOf(int region) => region < _componentOf.Count ? _componentOf[region] : UNSEEN;

        /// <summary>The one copy kept of a set of regions: equal sets that walks and components reach share one array.</summary>
        /// <param name="reached">The set, kept as it is when no equal one is.</param>
        private ulong[] Keep(ulong[] reached)
        {
            if (!_reaches.TryGetValue(reached, out var kept))
                _reaches.Add(reached, kept = reached);
            return kept;
        }

        /// <summary>Walks the reach of starts in turn, sharing what is visited: what an effect from them reaches, as bits, and whether
        /// the walk of a receiver seen through its declaring type met that receiver again. Only a call walked whole walks so
        /// (<see cref="BitsOf"/>).</summary>
        /// <param name="starts">The starts, each a region and the type restricting it or null.</param>
        private Walked Walk(IReadOnlyList<(int Region, string? DeclaringTypeKey)> starts)
        {
            var reached = Bits.EMPTY;
            var visited = Bits.EMPTY;
            var restrictedVisited = new HashSet<(int, string)>();
            var metItself = false;
            var pending = new Stack<int>();
            foreach (var (start, declaringTypeKey) in starts)
            {
                WorkCancellation.Check();
                pending.Push(start);
                while (pending.TryPop(out var region))
                {
                    WorkCancellation.Check();
                    // A receiver seen as its declaring type only is not yet seen whole: the same object handed as an argument still is.
                    var isRestrictedStart = region == start && declaringTypeKey is not null;
                    if (isRestrictedStart ? !restrictedVisited.Add((region, declaringTypeKey!)) : !Bits.Add(ref visited, region))
                    {
                        metItself |= isRestrictedStart;
                        continue;
                    }
                    _visits++;
                    var step = StepOf(region);
                    // An immutable pair still holds its key and value, which the effect reaches as it reaches a collection's.
                    if (step.IsImmutable)
                        continue;
                    if (step.Captures is { } captures)
                    {
                        foreach (var captured in captures)
                        {
                            WorkCancellation.Check();
                            _edges++;
                            pending.Push(captured);
                        }
                        continue;
                    }

                    // A receiver is seen only through the state of the type declaring the member, and only where the walk starts.
                    var restricted = isRestrictedStart
                        ? Seen(FieldsOf(RegionId(region), declaringTypeKey), declaringTypeKey).Select(FieldSlot.Key).ToHashSet(StringComparer.Ordinal)
                        : null;
                    if (step.IsCollection || restricted?.Count > 0 || restricted is null && step.HasState)
                        Bits.Add(ref reached, region);
                    // What a collection holds is in its storages, which the heap holds as it holds any field (ADR 0010, phase 5b second
                    // run). A slot of a struct the object keeps is decided by the field, or the cell, the struct is kept in (R1).
                    foreach (var (root, targets) in step.Slots)
                    {
                        WorkCancellation.Check();
                        if (restricted is null || restricted.Contains(root) || root == PathValue.KEPT || step.IsCollection && PathValue.IsStorage(root))
                        {
                            foreach (var target in targets)
                            {
                                WorkCancellation.Check();
                                _edges++;
                                pending.Push(target);
                            }
                        }
                    }
                }
            }

            var kept = Keep(reached);
            _answers.Add(kept);
            return new Walked(kept, metItself);
        }

        /// <summary>The fields of an object an unknown effect starting at it sees: all of them, or, through the receiver of a member a
        /// type of the run's own declares, only those that type and its bases declare (R1, <see cref="UnseenWrites.Seen"/>).</summary>
        /// <param name="fields">The object's fields.</param>
        /// <param name="declaringTypeKey">The type declaring the member the object is the receiver of, or null for an object seen whole.</param>
        public IReadOnlyList<IrFieldRef> Seen(IReadOnlyList<IrFieldRef> fields, string? declaringTypeKey)
        {
            WorkCancellation.Check();
            return UnseenWrites.Seen(scope.Program, fields, declaringTypeKey);
        }

        /// <summary>The fields of types from metadata the program names on an object, its own or inherited: what the heap knows of the
        /// state a library type gives it (R1).</summary>
        /// <param name="regionId">The object's region.</param>
        public IReadOnlyList<IrFieldRef> LibraryFieldsOf(string regionId) => _libraryFields.Value.GetValueOrDefault(regionId) ?? [];

        /// <summary>What a walk of reach learns at a region, asked of the heap once per region: whether it is a collection, an immutable
        /// object or a delegate (with what it captures), whether it has state an effect reaches, and its slots' roots with their targets,
        /// regions as numbers.</summary>
        /// <param name="number">The region's number.</param>
        private RegionStep StepOf(int number)
        {
            while (_steps.Count <= number)
            {
                WorkCancellation.Check();
                _steps.Add(null);
            }
            if (_steps[number] is { } step)
                return step;
            var regionId = RegionId(number);
            var region = heap.Regions[regionId];
            var isCollection = InterproceduralAccesses.CollectionKindOf(heap, scope.Program, regionId).IsCollection;
            step = !isCollection && region.TypeKey is { } key && scope.Program.ImmutableTypeKeys.Contains(key)
                ? new RegionStep(isCollection, true, null, false, [])
                : region.Kind == HeapRegionKind.Delegate
                    ? new RegionStep(isCollection, false, heap.DelegateCaptures(regionId).Select(Number).ToArray(), false, [])
                    // A struct copy is no location and a struct's storage none of its own (R1, R2): the walk goes on through what their
                    // reference fields hold.
                    : new RegionStep(isCollection, false, null,
                                     region.Kind is not (HeapRegionKind.StructCopy or HeapRegionKind.StructStorage) &&
                                     (IsSourceObject(regionId) || LibraryFieldsOf(regionId).Count != 0),
                                     heap.FieldsOf(regionId).Select(slot => new SlotTargets(FieldSlot.Root(slot), heap.PointsTo(regionId, slot).Select(Number).ToArray()))
                                         .ToArray());
            _steps[number] = step;
            return step;
        }

        private readonly Dictionary<string, int> _numbers = new(StringComparer.Ordinal);
        private readonly List<string> _regionIds = [];
        private readonly List<RegionStep?> _steps = [];
        private readonly Dictionary<(int Region, string? DeclaringTypeKey), Walked> _walks = [];
        private readonly Dictionary<ulong[], ulong[]> _reaches = new(ExecutionIds.BitsComparer.INSTANCE);
        // The answers of starts and of calls walked whole, each a kept array, so distinct by reference.
        private readonly HashSet<ulong[]> _answers = new(ReferenceEqualityComparer.Instance);
        // The walk of components, by region number: its component or its state (UNSEEN, OPEN), its order of meeting and its lowest link.
        private readonly List<int> _componentOf = [];
        private readonly List<int> _order = [];
        private readonly List<int> _low = [];
        private readonly Stack<int> _open = new();
        private int _opened;
        // By component: what it reaches, the last component that took it as one it points to, the first word of what it reaches that
        // holds a region, the component that keeps that array, and a region with state of it, or -1 when it has none.
        private readonly List<ulong[]> _componentReaches = [];
        private readonly List<int> _stamps = [];
        private readonly List<int> _firstWords = [];
        private readonly List<int> _keepers = [];
        private readonly List<int> _representatives = [];
        // The component that keeps each array components reach: the first that reached it.
        private readonly Dictionary<ulong[], int> _keeperOf = new(ReferenceEqualityComparer.Instance);
        // The walks of calls walked whole, by their starts in turn.
        private readonly Dictionary<string, Walked> _callWalks = new(StringComparer.Ordinal);
        private long _calls, _wholeWalks, _visits, _edges, _restrictedVisits, _components, _unions, _skippedUnions, _unionWords;

        /// <summary>A region no walk of components has met.</summary>
        private const int UNSEEN = -1;

        /// <summary>A region a walk of components has met and whose component is not complete yet.</summary>
        private const int OPEN = -2;

        /// <summary>A region in a walk of components, with how far its successors are taken: the captures of a delegate counted down in
        /// <see cref="Target"/>, or the slots counted down in <see cref="Slot"/> and the targets of the current one in
        /// <see cref="Target"/>.</summary>
        private struct Frame
        {
            public int Region;
            public int Slot;
            public int Target;
        }

        /// <summary>What a walk of reach learns at one region.</summary>
        /// <param name="IsCollection">Whether the region is a collection ADR 0010 models.</param>
        /// <param name="IsImmutable">Whether it is an immutable object that is no collection: the walk stops there.</param>
        /// <param name="Captures">What a delegate captures, as numbers, or null for any other region.</param>
        /// <param name="HasState">Whether it is an object of the run's own with state, or one whose library fields the program names.</param>
        /// <param name="Slots">Its slots, each by its root with the regions it points to.</param>
        private sealed record RegionStep(bool IsCollection, bool IsImmutable, int[]? Captures, bool HasState, SlotTargets[] Slots);

        /// <summary>A slot of a region, by its root (<see cref="FieldSlot.Root"/>), with the regions it points to.</summary>
        /// <param name="Root">The slot's root: its field, or the storage of a collection.</param>
        /// <param name="Targets">The regions it points to, as numbers.</param>
        private sealed record SlotTargets(string Root, int[] Targets);

        /// <summary>What a walk of reach found.</summary>
        /// <param name="Reached">The regions an effect from its starts reaches, as bits; one copy for every walk reaching the same.</param>
        /// <param name="MetItself">Whether the walk of a receiver seen through its declaring type met that receiver again.</param>
        private sealed record Walked(ulong[] Reached, bool MetItself);

        /// <summary>Whether a region is an object of a type of the run's own with state of its own: its fields are resources.</summary>
        public bool IsSourceObject(string regionId) =>
            heap.Regions[regionId].TypeKey is { } key && scope.Program.InstanceFieldsOf(key) is { Count: > 0 };

        /// <summary>The instance fields of types from metadata that the program's own accesses name, directly or through a reference, by
        /// the objects they name them on.</summary>
        /// <param name="scope">The scope whose program tells a type of the run's own from one from metadata.</param>
        /// <param name="heap">The solved heap whose instances' accesses name the fields and resolve the objects.</param>
        private static IReadOnlyDictionary<string, IReadOnlyList<IrFieldRef>> LibraryFields(ScopeProgram scope, HeapSolution heap)
        {
            WorkCancellation.Check();
            var named = heap.Instances.Values.SelectMany(instance =>
                UnseenWrites.LibraryFields(scope.Program, instance.Summary, instance.Substitution)
                            .Select(item => (Instance: instance, item.Field, item.Bases)));
            var fields = new Dictionary<string, Dictionary<string, IrFieldRef>>(StringComparer.Ordinal);
            foreach (var (instance, field, bases) in named)
            {
                WorkCancellation.Check();
                foreach (var region in bases.Where(value => value is not PathValue { IsWildcard: true })
                                            .SelectMany(value => heap.Resolve(instance.Id, value)))
                {
                    WorkCancellation.Check();
                    if (!fields.TryGetValue(region, out var byKey))
                        fields.Add(region, byKey = new Dictionary<string, IrFieldRef>(StringComparer.Ordinal));
                    byKey.TryAdd(FieldSlot.Key(field), field);
                }
            }

            return fields.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<IrFieldRef>)pair.Value.Values.ToArray(), StringComparer.Ordinal);
        }
    }

    /// <summary>Where an unknown effect starts: the values, the collection an argument is where the body names one, and, for a
    /// receiver, the type of the run's own declaring the member, whose state alone it sees.</summary>
    /// <param name="Values">The values handed to the unknown code.</param>
    /// <param name="Collection">The collection the argument names, when known.</param>
    /// <param name="DeclaringTypeKey">The type restricting a receiver's visible state, or null.</param>
    public sealed record Start(IReadOnlySet<AbstractValue> Values, ArgumentCollection? Collection, string? DeclaringTypeKey)
    {
        /// <summary>Whether the start holds only fresh objects, created in the calling body or handed down through calls the creating
        /// invocation waits for: the effect's accesses of their own fields are accesses to fresh objects.</summary>
        public bool IsFresh { get; init; }
    }
}
