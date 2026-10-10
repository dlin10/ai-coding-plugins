using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Accesses;

/// <summary>An unresolved call of one instance (R1): an opaque call no library model describes, a dispatch with no receiver
/// object, an operation on a <c>dynamic</c> value, or a locator nothing resolves. <see cref="Receivers"/> are seen only through the
/// state of <see cref="DeclaringTypeKey"/>; <see cref="Arguments"/> are seen whole. A locator makes a semantic gap and has no unknown
/// effect: the DI model keeps it (R1, R4).</summary>
public sealed record UnknownCall(MethodInstance Instance, int OperationId, string Callee, string Kind, IReadOnlySet<AbstractValue> Receivers,
                                 string? DeclaringTypeKey, IReadOnlyList<CallArgument> Arguments, IReadOnlyList<DelegateCreationValue> Delegates)
{
    public IrProvenance? Provenance { get; init; }
    public IReadOnlyList<SummaryPredicate> Conditions { get; init; } = [];
    public bool IsLocator => Kind == SemanticGapKinds.MODEL_GAP;

    /// <summary>The escaped library sequence whose unknown enumeration makes this call, an unresolved iterator delegate of it or of a
    /// sequence it enumerates; null for a call made where it stands (R3, R5).</summary>
    public string? EnumeratedSequence { get; init; }
}

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
            var summary = instance.Summary;
            foreach (var call in summary.OpaqueCalls.Where(call => !call.IsKnown))
            {
                var isLocator = call.ServiceCall is { Kind: not IrServiceCallKind.ScopeCreation } && locators.Contains((instance.BodyId, call.OperationId));
                if (!isLocator && modelled.Contains(instance.BodyId, call))
                    continue;
                // A call through an interface is the member of the table each object it decides implements it with, and stays unresolved
                // for the others alone (ADR 0010, amendment of the phase 5b third run).
                var receivers = call.Receivers;
                var projected = CollectionObjects.LibraryCalls(call, value => heap.Resolve(instance.Id, value),
                    region => CollectionObjects.KindOf(heap.Regions[region], scope.Program, scope.Summaries)).ToArray();
                foreach (var direct in projected.Where(direct => !direct.IsKnown))
                    AddLibraryUnknown(direct);
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
                    Conditions = call.Conditions
                });
            }

            // A dispatch sees the receiver objects whose implementation has no body through the type declaring that implementation; one
            // with no receiver object at all sees only its arguments (R1).
            foreach (var call in summary.Calls.Where(call => heap.UnresolvedDispatches.Contains((instance.Id, call.OperationId))))
            {
                foreach (var direct in CollectionObjects.LibraryCalls(call, value => heap.Resolve(instance.Id, value),
                    region => CollectionObjects.KindOf(heap.Regions[region], scope.Program, scope.Summaries, call.Target)).Where(direct => !direct.IsKnown))
                    AddLibraryUnknown(direct);
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
                                                          Conditions = call.Conditions
                                                      }));
            }

            bool HasLibrary(IReadOnlyList<IrImplementation> implementations, string region, string? method) =>
                implementations.Any(implementation => implementation.Library is not null && implementation.Kind ==
                    CollectionObjects.KindOf(heap.Regions[region], scope.Program, scope.Summaries, method));

            void AddLibraryUnknown(SummaryOpaqueCall direct) => calls.Add(new UnknownCall(instance, direct.OperationId, direct.Callee,
                KindOf(direct.Callee), direct.Receivers, direct.DeclaringTypeKey, direct.Arguments, direct.Delegates)
            {
                Provenance = direct.Provenance,
                Conditions = direct.Conditions
            });
            // A factory of `GetOrAdd` or `AddOrUpdate` whose body the heap has not runs as a call of the delegate would: an unresolved
            // dispatch, which sees what the factories that ran no body are handed — the key, the overload's argument, and, for an update
            // factory alone, the value the dictionary holds (R11).
            foreach (var call in summary.OpaqueCalls.Where(call => InterproceduralAccesses.RunsFactories(call.Collection) &&
                                                                   heap.UnresolvedDispatches.Contains((instance.Id, call.OperationId))))
            {
                var member = call.Collection!;
                var inputs = heap.UnresolvedFactoryInputs.GetValueOrDefault((instance.Id, call.OperationId)) ?? new HashSet<IrFactoryInput>();
                var handed = call.Arguments.Where(argument => !member.Factories.Contains(argument.ParameterOrdinal) &&
                                                              (argument.ParameterOrdinal == member.KeyArgument && inputs.Contains(IrFactoryInput.Key) ||
                                                               argument.ParameterOrdinal == member.FactoryArgument && inputs.Contains(IrFactoryInput.Argument)))
                                 .ToList();
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
                                                                                            [new CallArgument(0, operation.Values)], [])
                                                        {
                                                            Provenance = operation.Provenance,
                                                            Conditions = operation.Conditions
                                                        }));
        }

        // An escaped library sequence is enumerated by an unknown execution as well, and there each iterator delegate no body resolves,
        // of it or of a sequence it enumerates, is an unresolved dispatch as at every other enumeration (R3, R5).
        foreach (var escaped in heap.LibrarySequences.Values.Where(sequence => !sequence.IsGrouping && heap.UnknownIterators.Contains(sequence.RegionId))
                                    .OrderBy(sequence => sequence.RegionId, StringComparer.Ordinal))
        {
            foreach (var sequence in EnumeratedSequences(heap, escaped.RegionId, new HashSet<string>(StringComparer.Ordinal)))
            {
                if (!heap.Instances.TryGetValue(sequence.CreatorInstance, out var creator))
                    continue;
                var provenance = creator.Summary.OpaqueCalls.FirstOrDefault(call => call.OperationId == sequence.CreatorOperation)?.Provenance;
                foreach (var dispatch in sequence.Unresolved)
                {
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

    /// <summary>A library sequence and every library sequence enumerating it enumerates, through its sources.</summary>
    internal static IEnumerable<LibrarySequence> EnumeratedSequences(HeapSolution heap, string region, HashSet<string> visited)
    {
        if (!visited.Add(region) || !heap.LibrarySequences.TryGetValue(region, out var sequence) || sequence.IsGrouping)
            yield break;
        yield return sequence;
        foreach (var source in sequence.Sources)
        {
            foreach (var enumerated in EnumeratedSequences(heap, source, visited))
                yield return enumerated;
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

        public bool Contains(string bodyId, SummaryOpaqueCall call) =>
            call.IsRecognized || call.Collection is not null || call.ServiceCall is not null ||
            _registrations.Contains((_memberOf.GetValueOrDefault(bodyId) ?? bodyId, call.OperationId)) ||
            IsMapping(call.Callee) && call.Delegates.Any(created => _minimalApiBodies.Contains(created.Target));

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

    private static string KindOf(string callee) =>
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
    public sealed class Reach(ScopeProgram scope, HeapSolution heap)
    {
        private readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<IrFieldRef>>> _libraryFields = new(() => LibraryFields(scope, heap));

        /// <summary>The fields of an object an unknown effect may see: those its type of the run's own declares, and the fields of types
        /// from metadata the program names on it (R1). A restricted receiver is seen through its recorded declaring type,
        /// including when a DI region is labelled with the service interface.</summary>
        /// <param name="regionId">The receiver or argument region.</param>
        /// <param name="declaringTypeKey">The declaring type through which a receiver is seen, or null for an unrestricted object.</param>
        public IReadOnlyList<IrFieldRef> FieldsOf(string regionId, string? declaringTypeKey = null) =>
            [.. ((declaringTypeKey ?? heap.Regions[regionId].TypeKey) is { } key ? scope.Program.InstanceFieldsOf(key) : null) ?? [],
             .. LibraryFieldsOf(regionId)];

        /// <summary>The values an unknown effect starts from: the receivers it may see, and every argument, an array created for the
        /// call by its elements. Through a receiver of a member a type of the run's own declares, only that type's state and its bases'
        /// is seen: <see cref="Start.DeclaringTypeKey"/> names it.</summary>
        public IEnumerable<Start> Starts(UnknownCall call)
        {
            var declaring = call.DeclaringTypeKey is { } key && scope.Program.Type(key) is { IsSource: true } ? key : null;
            if (declaring is not null)
            {
                if (call.Receivers.Count != 0)
                    yield return new Start(call.Receivers, null, declaring);
            }
            else
            {
                // A library member sees an object of the run's own only through the state of the library type declaring it, and a
                // library object whole, as far as the heap knows it.
                var ownObjects = call.Receivers.Where(value => heap.Resolve(call.Instance.Id, value).Any(IsSourceObject)).ToHashSet();
                var libraryObjects = call.Receivers.Except(ownObjects).ToHashSet();
                if (libraryObjects.Count != 0)
                    yield return new Start(libraryObjects, null, null);
                if (ownObjects.Count != 0 && call.DeclaringTypeKey is { } library)
                    yield return new Start(ownObjects, null, library);
            }

            foreach (var argument in call.Arguments)
                yield return argument.CreatedElements is { } elements
                    ? new Start(elements, null, null)
                    : new Start(argument.Values, argument.Collection, null) { IsFresh = argument.IsFresh };
            foreach (var created in call.Delegates)
                yield return new Start(new HashSet<AbstractValue> { created }, null, null);
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
        public (IReadOnlySet<string> Regions, bool TakesDelegate) Of(UnknownCall call)
        {
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var start in Starts(call))
            {
                foreach (var region in start.Values.SelectMany(value => heap.Resolve(call.Instance.Id, value)).Distinct(StringComparer.Ordinal))
                    Visit(region, reached, visited, start.DeclaringTypeKey);
            }

            return (reached, Delegates(call).Any());
        }

        /// <summary>The fields of an object an unknown effect starting at it sees: all of them, or, through the receiver of a member a
        /// type of the run's own declares, only those that type and its bases declare (R1).</summary>
        public IReadOnlyList<IrFieldRef> Seen(IReadOnlyList<IrFieldRef> fields, string? declaringTypeKey)
        {
            if (declaringTypeKey is null)
                return fields;
            var declared = new HashSet<string>(StringComparer.Ordinal);
            for (var key = declaringTypeKey; key is not null && declared.Add(FieldSlot.WithoutTypeArguments(key)); key = scope.Program.Type(key)?.BaseTypeKey)
            {
            }

            return fields.Where(field => declared.Contains(FieldSlot.TypeKey(field.Assembly, field.ContainingTypeId))).ToArray();
        }

        /// <summary>The fields of types from metadata the program names on an object, its own or inherited: what the heap knows of the
        /// state a library type gives it (R1).</summary>
        public IReadOnlyList<IrFieldRef> LibraryFieldsOf(string regionId) => _libraryFields.Value.GetValueOrDefault(regionId) ?? [];

        private void Visit(string start, HashSet<string> reached, HashSet<string> visited, string? declaringTypeKey)
        {
            var pending = new Stack<string>([start]);
            while (pending.TryPop(out var regionId))
            {
                // A receiver seen as its declaring type only is not yet seen whole: the same object handed as an argument still is.
                var isRestrictedStart = regionId == start && declaringTypeKey is not null;
                if (!visited.Add(isRestrictedStart ? $"{regionId}|{declaringTypeKey}" : regionId))
                    continue;
                var step = StepOf(regionId);
                // An immutable pair still holds its key and value, which the effect reaches as it reaches a collection's.
                if (step.IsImmutable)
                    continue;
                if (step.Captures is { } captures)
                {
                    foreach (var captured in captures)
                        pending.Push(captured);
                    continue;
                }

                // A receiver is seen only through the state of the type declaring the member, and only where the walk starts.
                var restricted = isRestrictedStart
                    ? Seen(FieldsOf(regionId, declaringTypeKey), declaringTypeKey).Select(FieldSlot.Key).ToHashSet(StringComparer.Ordinal)
                    : null;
                if (step.IsCollection || restricted?.Count > 0 || restricted is null && step.HasState)
                    reached.Add(regionId);
                // What a collection holds is in its storages, which the heap holds as it holds any field (ADR 0010, phase 5b second run).
                foreach (var (slot, targets) in step.Slots)
                {
                    if (restricted is null || restricted.Contains(slot) || slot == PathValue.KEPT || step.IsCollection && PathValue.IsStorage(slot))
                    {
                        foreach (var target in targets)
                            pending.Push(target);
                    }
                }
            }
        }

        /// <summary>What a walk of reach learns at a region, asked of the heap once per region: whether it is a collection, an immutable
        /// object or a delegate (with what it captures), whether it has state an effect reaches, and its slots with their targets.</summary>
        /// <param name="regionId">The region.</param>
        private RegionStep StepOf(string regionId)
        {
            if (_steps.TryGetValue(regionId, out var step))
                return step;
            var region = heap.Regions[regionId];
            var isCollection = InterproceduralAccesses.CollectionKindOf(heap, scope.Program, regionId).IsCollection;
            step = !isCollection && region.TypeKey is { } key && scope.Program.ImmutableTypeKeys.Contains(key)
                ? new RegionStep(isCollection, true, null, false, [])
                : region.Kind == HeapRegionKind.Delegate
                    ? new RegionStep(isCollection, false, heap.DelegateCaptures(regionId).ToArray(), false, [])
                    : new RegionStep(isCollection, false, null, IsSourceObject(regionId) || LibraryFieldsOf(regionId).Count != 0,
                                     heap.FieldsOf(regionId).Select(slot => new SlotTargets(slot, heap.PointsTo(regionId, slot).ToArray())).ToArray());
            _steps.Add(regionId, step);
            return step;
        }

        private readonly Dictionary<string, RegionStep> _steps = new(StringComparer.Ordinal);

        /// <summary>What a walk of reach learns at one region.</summary>
        /// <param name="IsCollection">Whether the region is a collection ADR 0010 models.</param>
        /// <param name="IsImmutable">Whether it is an immutable object that is no collection: the walk stops there.</param>
        /// <param name="Captures">What a delegate captures, or null for any other region.</param>
        /// <param name="HasState">Whether it is an object of the run's own with state, or one whose library fields the program names.</param>
        /// <param name="Slots">Its slots, each with the regions it points to.</param>
        private sealed record RegionStep(bool IsCollection, bool IsImmutable, string[]? Captures, bool HasState, SlotTargets[] Slots);

        /// <summary>A slot of a region, with the regions it points to.</summary>
        /// <param name="Slot">The slot.</param>
        /// <param name="Targets">The regions it points to.</param>
        private sealed record SlotTargets(string Slot, string[] Targets);

        /// <summary>Whether a region is an object of a type of the run's own with state of its own: its fields are resources.</summary>
        public bool IsSourceObject(string regionId) =>
            heap.Regions[regionId].TypeKey is { } key && scope.Program.InstanceFieldsOf(key) is { Count: > 0 };

        /// <summary>The instance fields of types from metadata that the program's own accesses name, directly or through a reference, by
        /// the objects they name them on.</summary>
        private static IReadOnlyDictionary<string, IReadOnlyList<IrFieldRef>> LibraryFields(ScopeProgram scope, HeapSolution heap)
        {
            var named = heap.Instances.Values.SelectMany(instance =>
                    instance.Summary.Accesses.Select(access => (Instance: instance, access.Field, access.Bases))
                            .Concat(instance.Summary.ReferenceAccesses.SelectMany(access => access.Targets).OfType<ReferenceCell>()
                                            .Select(cell => (Instance: instance, cell.Field, cell.Bases))))
                .Where(item => !item.Field.IsStatic && item.Field.Kind == IrFieldKind.Field &&
                               scope.Program.Type(FieldSlot.TypeKey(item.Field.Assembly, item.Field.ContainingTypeId))?.IsSource != true);
            var fields = new Dictionary<string, Dictionary<string, IrFieldRef>>(StringComparer.Ordinal);
            foreach (var (instance, field, bases) in named)
            {
                foreach (var region in bases.Where(value => value is not PathValue { IsWildcard: true })
                                            .SelectMany(value => heap.Resolve(instance.Id, value)))
                {
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
    public sealed record Start(IReadOnlySet<AbstractValue> Values, ArgumentCollection? Collection, string? DeclaringTypeKey)
    {
        /// <summary>Whether the start is an argument holding only objects the calling body created: the effect's accesses of their
        /// own fields are accesses to fresh objects (R12).</summary>
        public bool IsFresh { get; init; }
    }
}
