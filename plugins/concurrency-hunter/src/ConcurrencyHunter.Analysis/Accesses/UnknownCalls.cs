using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Accesses;

/// <summary>An unresolved call of one instance (R1): an opaque call the library table does not describe, a dispatch with no receiver
/// object, an operation on a <c>dynamic</c> value, or a locator nothing resolves. <see cref="Receivers"/> are seen only through the
/// state of <see cref="DeclaringTypeKey"/>; <see cref="Arguments"/> are seen whole. A locator makes a semantic gap and has no unknown
/// effect: the DI model keeps it (R1, R4).</summary>
public sealed record UnknownCall(MethodInstance Instance, int OperationId, string Callee, string Kind, IReadOnlySet<AbstractValue> Receivers,
                                 string? DeclaringTypeKey, IReadOnlyList<CallArgument> Arguments, IReadOnlyList<DelegateCreationValue> Delegates)
{
    public IrProvenance? Provenance { get; init; }
    public IReadOnlyList<SummaryPredicate> Conditions { get; init; } = [];
    public bool IsLocator => Kind == SemanticGapKinds.MODEL_GAP;
}

/// <summary>The unresolved calls of a scope and what their unknown effects reach (R1). A call a recognizer of phases 1-4 models is not
/// unresolved: a member of a type one owns or a framework slice, a collection member, a DI registration or scope call, or a <c>Map*</c>
/// whose delegate the roots provider made a root.</summary>
public static class UnknownCalls
{
    private const string REFLECTION_NAMESPACE = "System.Reflection.";
    private const string ACTIVATOR = "System.Activator.";

    /// <summary>Every unresolved call of every instance the heap reached, in instance and operation order.</summary>
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
                // A constructor sees nothing through the object it creates: that object holds only what its arguments give it yet.
                calls.Add(new UnknownCall(instance, call.OperationId, call.Callee, isLocator ? SemanticGapKinds.MODEL_GAP : KindOf(call.Callee),
                                          call.IsConstructor ? new HashSet<AbstractValue>() : call.Receivers, call.DeclaringTypeKey, call.Arguments,
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
                var receivers = heap.UnresolvedDispatchReceivers.GetValueOrDefault((instance.Id, call.OperationId)) ?? new HashSet<(string, string?)>();
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
            // A `dynamic` receiver is seen whole, like the arguments and the value assigned: all of them are the operation's operands.
            calls.AddRange(summary.DynamicOperations.Select(operation => new UnknownCall(instance, operation.OperationId, operation.Callee, SemanticGapKinds.DYNAMIC,
                                                                                            new HashSet<AbstractValue>(), null,
                                                                                            [new CallArgument(0, operation.Values)], [])
                                                        {
                                                            Provenance = operation.Provenance,
                                                            Conditions = operation.Conditions
                                                        }));
        }

        return calls.OrderBy(call => call.Instance.Id, StringComparer.Ordinal).ThenBy(call => call.OperationId).ToArray();
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
        private readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<(MethodInstance Instance, SummaryOpaqueCall Call)>>> _insertions =
            new(() => InterproceduralAccesses.Insertions(heap));
        private readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<IrFieldRef>>> _libraryFields = new(() => LibraryFields(scope, heap));

        /// <summary>The fields of an object an unknown effect may see: those its type of the run's own declares, and the fields of types
        /// from metadata the program names on it (R1).</summary>
        public IReadOnlyList<IrFieldRef> FieldsOf(string regionId) =>
            [.. (heap.Regions[regionId].TypeKey is { } key ? scope.Program.InstanceFieldsOf(key) : null) ?? [], .. LibraryFieldsOf(regionId)];

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
                yield return argument.CreatedElements is { } elements ? new Start(elements, null, null) : new Start(argument.Values, argument.Collection, null);
            foreach (var created in call.Delegates)
                yield return new Start(new HashSet<AbstractValue> { created }, null, null);
        }

        /// <summary>The delegate regions the call is handed, directly or in an array it is handed: each runs in an unknown execution of
        /// its own (R3).</summary>
        public IEnumerable<string> Delegates(UnknownCall call)
        {
            var handed = Starts(call).SelectMany(start => start.Values).SelectMany(value => heap.Resolve(call.Instance.Id, value))
                                     .ToHashSet(StringComparer.Ordinal);
            return handed.Concat(handed.Where(region => heap.Regions[region].TypeKey?.EndsWith(']') == true)
                                       .SelectMany(array => heap.PointsTo(array, PathValue.ELEMENT)))
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
                var region = heap.Regions[regionId];
                if (region.TypeKey is { } key && scope.Program.ImmutableTypeKeys.Contains(key))
                    continue;
                if (region.Kind == HeapRegionKind.Delegate)
                {
                    foreach (var captured in heap.DelegateCaptures(regionId))
                        pending.Push(captured);
                    continue;
                }

                // A receiver is seen only through the state of the type declaring the member, and only where the walk starts.
                var restricted = isRestrictedStart
                    ? Seen(FieldsOf(regionId), declaringTypeKey).Select(FieldSlot.Key).ToHashSet(StringComparer.Ordinal)
                    : null;
                var isCollection = InterproceduralAccesses.CollectionKindOf(heap, scope.Program, regionId).IsCollection;
                if (isCollection || restricted?.Count > 0 || restricted is null && (IsSourceObject(regionId) || LibraryFieldsOf(regionId).Count != 0))
                    reached.Add(regionId);
                foreach (var target in heap.FieldsOf(regionId).Where(slot => restricted is null || restricted.Contains(slot))
                                           .SelectMany(slot => heap.PointsTo(regionId, slot)))
                    pending.Push(target);
                if (!isCollection)
                    continue;
                foreach (var (inserter, insertion) in _insertions.Value.GetValueOrDefault(regionId) ?? [])
                {
                    foreach (var held in insertion.Arguments.Where(argument => InterproceduralAccesses.IsHeldArgument(insertion.Collection, argument.ParameterOrdinal))
                                                  .SelectMany(argument => argument.Values)
                                                  .SelectMany(value => heap.Resolve(inserter.Id, value)))
                        pending.Push(held);
                }
            }
        }

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
    public sealed record Start(IReadOnlySet<AbstractValue> Values, ArgumentCollection? Collection, string? DeclaringTypeKey);
}
