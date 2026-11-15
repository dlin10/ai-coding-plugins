using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Heap;

namespace ConcurrencyHunter.Accesses;

/// <summary>The library-created objects no place of the program holds directly, for one solved scope.</summary>
internal sealed class LibraryInternalObjects
{
    private readonly HashSet<string> _internal = new(StringComparer.Ordinal);

    internal LibraryInternalObjects(HeapSolution heap, CallSiteAdmission admission)
    {
        var libraryCopies = new HashSet<string>(StringComparer.Ordinal);
        var programHeld = new HashSet<string>(StringComparer.Ordinal);
        var copies = new Queue<string>();
        var programCopies = new Queue<string>();
        var regions = heap.Regions.Values.ToArray();

        // Copies have no creation site: their holders decide which side their slots belong to.
        foreach (var instance in heap.Instances.Values)
        {
            WorkCancellation.Check();
            var library = admission.IsLibraryBody(instance.BodyId);
            foreach (var target in Values(instance).SelectMany(value => heap.Resolve(instance.Id, value))
                                                   .Concat(instance.Receivers).Concat(instance.Parameters.Values.SelectMany(values => values)))
            {
                WorkCancellation.Check();
                Hold(target, library);
            }
        }
        foreach (var region in regions.Where(region => region.Kind is not (HeapRegionKind.StructCopy or HeapRegionKind.StructStorage)))
        {
            WorkCancellation.Check();
            if (BelongsToLibrary(region))
                Slots(region.Identity, library: true);
        }
        FollowCopies(library: true);

        foreach (var region in regions)
        {
            WorkCancellation.Check();
            if (!BelongsToLibrary(region))
                Slots(region.Identity, library: false);
        }
        FollowCopies(library: false);

        var openGroups = regions.Where(region => region.IsOpen).Select(region => region.Group).ToHashSet(StringComparer.Ordinal);
        foreach (var region in regions)
        {
            WorkCancellation.Check();
            if (region.Kind is HeapRegionKind.Allocation or HeapRegionKind.Closure or HeapRegionKind.Delegate or HeapRegionKind.Task or HeapRegionKind.Symbolic &&
                admission.IsLibraryBody(region.SiteBodyId) && !region.IsMerged && !openGroups.Contains(region.Group) &&
                !programHeld.Contains(region.Identity))
                _internal.Add(region.Identity);
        }

        bool IsCopy(string id) => heap.Located(id, string.Empty, copies: true) is { } located &&
                                 heap.Regions[located.Region].Kind == HeapRegionKind.StructCopy;

        bool BelongsToLibrary(HeapRegion region)
        {
            WorkCancellation.Check();
#pragma warning disable CS8524 // Every named kind is explicit; an unnamed enum value throws instead of acquiring an owner.
            return region.Kind switch
            {
                HeapRegionKind.Static => region.TypeKey is { } type && admission.IsLibraryType(type),
                HeapRegionKind.Di => false,
                HeapRegionKind.Allocation => admission.IsLibraryBody(region.SiteBodyId),
                HeapRegionKind.Receiver => false,
                HeapRegionKind.Delegate => admission.IsLibraryBody(region.SiteBodyId),
                HeapRegionKind.Container => false,
                HeapRegionKind.Provider => false,
                HeapRegionKind.Symbolic => admission.IsLibraryBody(region.SiteBodyId),
                HeapRegionKind.Task => admission.IsLibraryBody(region.SiteBodyId),
                HeapRegionKind.StructStorage => heap.Located(region.Identity, string.Empty, copies: true) is { } located &&
                                                BelongsToLibrary(heap.Regions[located.Region]),
                HeapRegionKind.StructCopy => libraryCopies.Contains(region.Identity) && !programHeld.Contains(region.Identity),
                HeapRegionKind.Closure => admission.IsLibraryBody(region.SiteBodyId)
            };
#pragma warning restore CS8524
        }

        void Hold(string target, bool library)
        {
            var id = heap.Located(target, string.Empty, copies: true)?.Region ?? target;
            if (library)
            {
                if (IsCopy(id) && libraryCopies.Add(id))
                    copies.Enqueue(id);
            }
            else if (programHeld.Add(id) && IsCopy(id))
                programCopies.Enqueue(id);
        }

        void Slots(string id, bool library)
        {
            foreach (var target in heap.FieldsOf(id).SelectMany(slot => heap.PointsTo(id, slot)))
            {
                WorkCancellation.Check();
                Hold(target, library);
            }
        }

        void FollowCopies(bool library)
        {
            var followed = new HashSet<string>(StringComparer.Ordinal);
            var pending = library ? copies : programCopies;
            while (pending.TryDequeue(out var id))
            {
                WorkCancellation.Check();
                if (followed.Add(id))
                    Slots(id, library);
            }
        }
    }

    internal bool IsInternal(string region) => _internal.Contains(region);

    private static IEnumerable<AbstractValue> Values(MethodInstance instance)
    {
        var summary = instance.Summary;
        return summary.Variables.SelectMany(value => value.Values)
                      .Concat(summary.Variables.Where(variable => variable.Cell is not null).Select(variable => new CapturedValue(variable.Cell!)))
                      .Concat(summary.Returns.SelectMany(value => value.Values))
                      .Concat(summary.RefParameters.SelectMany(value => value.Values))
                      .Concat(summary.Accesses.SelectMany(access => access.Bases.Concat(access.Values)))
                      .Concat(summary.CapturedStores.SelectMany(store => store.Values))
                      .Concat(summary.StructCopies.SelectMany(copy => copy.Targets.Concat(copy.Sources)))
                      .Concat(summary.Calls.SelectMany(call => call.Receivers.Concat(call.Arguments.SelectMany(argument => argument.Values))
                                                                 .Append(new CallResultValue(call.OperationId))))
                      .Concat(summary.OpaqueCalls.SelectMany(call => call.Receivers.Concat(call.Arguments.SelectMany(argument => argument.Values))
                                                                       .Append(new CallResultValue(call.OperationId))))
                      .Concat(summary.Stores.Concat(summary.ReferenceStores).SelectMany(store => store.Bases.Concat(store.Values)))
                      .Concat(summary.Elements.Concat(summary.ReferenceElementStores).SelectMany(element => element.Arrays.Concat(element.Values)))
                      .Concat(summary.ArgumentEffects.SelectMany(effect => effect.Values))
                      .Concat(summary.DynamicOperations.SelectMany(operation => operation.Values))
                      .Concat(summary.UnsupportedOperations.SelectMany(operation => operation.Values))
                      .Concat(summary.Delegates.SelectMany(transfer => transfer.Receivers.Append(transfer.Delegate)))
                      .Concat(summary.Locks.SelectMany(transfer => transfer.Values))
                      .Concat(EventValues(summary).OfType<SummaryValue>().SelectMany(value => value.Values))
                      .Concat(summary.Joins.Select(join => new AwaitResultValue(join.OperationId)))
                      .Concat(References(summary).SelectMany(target => new AbstractValue[]
                          { new ReferenceLocationValue(target, Read: false), new ReferenceLocationValue(target, Read: true) }))
                      .Concat(summary.Yields);
    }

    private static IEnumerable<ReferenceTarget> References(MethodSummary summary) =>
        summary.ReferenceAccesses.SelectMany(access => access.Targets)
               .Concat(summary.ReferenceReturns).Concat(summary.CollectionReturns)
               .Concat(summary.Calls.SelectMany(call => call.Arguments).Concat(summary.OpaqueCalls.SelectMany(call => call.Arguments))
                              .SelectMany(argument => argument.References.Concat(argument.Collection is { } collection ? [collection.Collection] : [])))
               .Concat(summary.ArgumentEffects.Where(effect => effect.Collection is not null).Select(effect => effect.Collection!.Collection));

    private static IEnumerable<SummaryValue?> EventValues(MethodSummary summary) =>
        summary.Spawns.SelectMany(spawn => spawn.Work.Cast<SummaryValue?>().Concat([spawn.Handle, spawn.State, spawn.Antecedent]))
               .Concat(summary.ThreadWorks.SelectMany(work => new SummaryValue?[] { work.Thread, work.Work }))
               .Concat(summary.Joins.SelectMany(join => join.Handles))
               .Concat(summary.WhenAlls.SelectMany(group => group.Tasks.Cast<SummaryValue?>().Append(group.Source)))
               .Concat(summary.Unwraps.Select(unwrap => unwrap.Outer))
               .Concat(summary.TaskOperations.SelectMany(operation => operation.Values.Cast<SummaryValue?>().Append(operation.Task)))
               .Concat(summary.Timers.SelectMany(timer => new[] { timer.Timer, timer.Callback, timer.State, timer.WaitHandle, timer.Result }));
}
