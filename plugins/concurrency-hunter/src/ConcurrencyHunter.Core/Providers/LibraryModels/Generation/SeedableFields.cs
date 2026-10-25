using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The one owner of whether a library field can receive a seed, is a path to further fields, or is a struct whose
/// reference fields setup seeds in place. Each answer starts with <see cref="TypeShape.Of"/>.</summary>
public static class SeedableFields
{
    /// <summary>The static storage loaded by the closure reached from a driver, keyed by its declaring type definition and name.</summary>
    /// <param name="run">The driver's pipeline run.</param>
    public static IReadOnlySet<(string Type, string Name)> LoadedStatics(ScopeRun run)
        => LoadedStorage(run, closed: false);

    /// <summary>The loaded static storage, preserving each constructed declaring type. All readers of recipe storage use this set.</summary>
    /// <param name="run">The driver's pipeline run.</param>
    public static IReadOnlySet<(string Type, string Name)> LoadedClosedStatics(ScopeRun run)
        => LoadedStorage(run, closed: true);

    private static IReadOnlySet<(string Type, string Name)> LoadedStorage(ScopeRun run, bool closed)
    {
        var loads = run.Executions?.Accesses.Where(access => access.Access.Kind == SummaryAccessKind.Load).ToArray() ?? [];
        // A load of a field of a struct a static field keeps loads that static's storage, which no field load names (R1).
        var fields = run.Reachable.Bodies.Values.SelectMany(body => body.Blocks).SelectMany(block => block.Operations)
                        .OfType<IrLoadFieldOperation>().Select(load => load.Field);
        var instances = run.Heap?.Instances.Values.ToArray() ?? [];
        var constructed = instances.SelectMany(instance => instance.Summary.Accesses.Where(access => access.Kind == SummaryAccessKind.Load)
            .SelectMany(access => access.Bases.SelectMany(StaticStorageOf).Prepend(access.Field))
            .Where(field => field.IsStatic)
            .Select(field => (ProgramIndex.Substitute($"{field.Assembly}:{field.ContainingTypeId}", instance.Substitution), field.Name)));
        var references = loads.Select(access => access.Access.Field)
                        .Concat(loads.SelectMany(access => access.Access.Bases).SelectMany(StaticStorageOf))
                        .Concat(ReferenceLoads(run, closed));
        // A struct a static field keeps loads that static's storage wherever a load reaches it, through a member called on the storage
        // or a reference to it too, where the load names the struct's own field: the static's slot is the first of the path (R1).
        var storages = loads.Where(access => access.RegionId is { } region && run.Heap?.Regions.TryGetValue(region, out var held) == true &&
                                             held.Kind == HeapRegionKind.Static && access.Slot.Contains(FieldSlot.PATH_SEPARATOR))
                            .Select(access => (closed ? run.Heap!.Regions[access.RegionId].TypeKey! : FieldSlot.DeclaringTypeKey(FieldSlot.Root(access.Slot)),
                                               FieldSlot.Name(FieldSlot.Root(access.Slot))));
        var storagesLoaded = fields.Concat(references).Where(field => field.IsStatic)
                     .Select(field => ($"{field.Assembly}:{field.ContainingTypeId}", field.Name))
                     .Concat(closed ? constructed : []).Concat(storages);
        return closed ? storagesLoaded.ToHashSet() :
            storagesLoaded.Select(storage => (run.Program.Decompose(storage.Item1).DefinitionKey, storage.Item2)).ToHashSet();
    }

    /// <summary>The static storage a field, auto-property or field-like event declares, in the same form as
    /// <see cref="LoadedStatics"/>: a field-like event's storage is named like the event.</summary>
    /// <param name="member">The field, auto-property or field-like event.</param>
    public static (string Type, string Name) StaticStorage(ISymbol member) =>
        (SymbolNames.TypeKey(member.ContainingType.OriginalDefinition), member.Name);

    /// <summary>The static field whose storage a value is a struct's storage in, through every struct around it: none for any other value.</summary>
    /// <param name="value">A base of an access.</param>
    private static IEnumerable<IrFieldRef> StaticStorageOf(AbstractValue value)
    {
        WorkCancellation.Check();
        return value switch
        {
            StructPlaceValue place => StaticStorageOf(place.Base),
            StaticHolderValue holder => [holder.Field],
            _ => []
        };
    }

    private static IEnumerable<IrFieldRef> ReferenceLoads(ScopeRun run, bool closed)
    {
        if (run.Heap is not { } heap)
            yield break;
        var outgoing = heap.Edges.ToLookup(edge => (edge.CallerInstance, edge.OperationId));
        var incoming = heap.Edges.ToLookup(edge => edge.CalleeInstance);
        var pending = new Queue<(MethodInstance Instance, ReferenceTarget Target)>(heap.Instances.Values.SelectMany(instance =>
            instance.Summary.ReferenceAccesses.Where(access => access.Kind == SummaryAccessKind.Load)
                    .SelectMany(access => access.Targets.Select(target => (instance, target)))));
        var seen = new HashSet<(string Instance, ReferenceTarget Target)>();
        while (pending.TryDequeue(out var item))
        {
            WorkCancellation.Check();
            var (instance, target) = item;
            if (!seen.Add((instance.Id, target)))
                continue;
            switch (target)
            {
                case ReferenceCell cell:
                    var key = ProgramIndex.Substitute($"{cell.Field.Assembly}:{cell.Field.ContainingTypeId}", instance.Substitution);
                    yield return closed ? cell.Field with { ContainingTypeIdentity = key[(cell.Field.Assembly.Length + 1)..] } : cell.Field;
                    break;
                case ReferenceCall call:
                    foreach (var edge in outgoing[(instance.Id, call.OperationId)])
                    {
                        WorkCancellation.Check();
                        var callee = heap.Instances[edge.CalleeInstance];
                        foreach (var returned in callee.Summary.ReferenceReturns)
                        {
                            WorkCancellation.Check();
                            pending.Enqueue((callee, returned));
                        }
                    }
                    break;
                case ReferenceParameter parameter:
                    foreach (var edge in incoming[instance.Id])
                    {
                        WorkCancellation.Check();
                        var caller = heap.Instances[edge.CallerInstance];
                        var arguments = caller.Summary.Calls.Where(call => call.OperationId == edge.OperationId)
                                              .SelectMany(call => call.Arguments)
                                              .Where(argument => argument.ParameterOrdinal == parameter.Ordinal);
                        foreach (var reference in arguments.SelectMany(argument => argument.References))
                        {
                            WorkCancellation.Check();
                            pending.Enqueue((caller, reference));
                        }
                    }
                    break;
            }
        }
    }

    private static readonly Dictionary<string, (string Concrete, string Insert, bool Dictionary)> Collections = new(StringComparer.Ordinal)
    {
        ["System.Collections.Generic.List`1"] = ("System.Collections.Generic.List`1", "Add", false),
        ["System.Collections.Generic.Dictionary`2"] = ("System.Collections.Generic.Dictionary`2", "set_Item", true),
        ["System.Collections.Concurrent.ConcurrentDictionary`2"] = ("System.Collections.Concurrent.ConcurrentDictionary`2", "TryAdd", true),
        ["System.Collections.Concurrent.ConcurrentQueue`1"] = ("System.Collections.Concurrent.ConcurrentQueue`1", "Enqueue", false),
        ["System.Collections.Concurrent.ConcurrentStack`1"] = ("System.Collections.Concurrent.ConcurrentStack`1", "Push", false),
        ["System.Collections.Concurrent.ConcurrentBag`1"] = ("System.Collections.Concurrent.ConcurrentBag`1", "Add", false),
        ["System.Collections.Generic.HashSet`1"] = ("System.Collections.Generic.HashSet`1", "Add", false),
        ["System.Collections.Generic.Queue`1"] = ("System.Collections.Generic.Queue`1", "Enqueue", false),
        ["System.Collections.Generic.Stack`1"] = ("System.Collections.Generic.Stack`1", "Push", false),
        ["System.Collections.Generic.LinkedList`1"] = ("System.Collections.Generic.LinkedList`1", "AddLast", false),
        ["System.Collections.Generic.IEnumerable`1"] = ("System.Collections.Generic.List`1", "Add", false),
        ["System.Collections.Generic.ICollection`1"] = ("System.Collections.Generic.List`1", "Add", false),
        ["System.Collections.Generic.IList`1"] = ("System.Collections.Generic.List`1", "Add", false),
        ["System.Collections.Generic.IReadOnlyCollection`1"] = ("System.Collections.Generic.List`1", "Add", false),
        ["System.Collections.Generic.IReadOnlyList`1"] = ("System.Collections.Generic.List`1", "Add", false),
        ["System.Collections.Generic.ISet`1"] = ("System.Collections.Generic.HashSet`1", "Add", false),
        ["System.Collections.Generic.IReadOnlySet`1"] = ("System.Collections.Generic.HashSet`1", "Add", false),
        ["System.Collections.Generic.IDictionary`2"] = ("System.Collections.Generic.Dictionary`2", "set_Item", true),
        ["System.Collections.Generic.IReadOnlyDictionary`2"] = ("System.Collections.Generic.Dictionary`2", "set_Item", true)
    };

    /// <summary>The ADR 0010 collection used to seed a field.</summary>
    /// <param name="ConcreteMetadataName">The concrete collection definition to construct.</param>
    /// <param name="InsertionMember">The member used to insert one value.</param>
    /// <param name="Dictionary">Whether the first axis is a key and the second a value.</param>
    /// <param name="Axes">The collection's element, or key and value, types.</param>
    public sealed record CollectionSeed(string ConcreteMetadataName, string InsertionMember, bool Dictionary,
                                        IReadOnlyList<ITypeSymbol> Axes);

    /// <summary>Whether a field of <paramref name="type"/> can hold a user object and receives a seed.</summary>
    /// <param name="type">The field or auto-property type.</param>
    public static bool Seed(ITypeSymbol type)
    {
        WorkCancellation.Check();
        var shape = TypeShape.Of(type);
        if (shape == TypeShapeKind.Delegate)
            return true;
        if (shape != TypeShapeKind.Reference)
            return false;
        if (type is ITypeParameterSymbol parameter)
            return !HasStructConstraint(parameter, []);
        if (type is IArrayTypeSymbol array)
            return Seed(array.ElementType);
        if (type.SpecialType == SpecialType.System_Object || type.TypeKind is TypeKind.Interface or TypeKind.Dynamic)
            return true;
        if (type is not INamedTypeSymbol named)
            return false;
        if (Collection(named) is { Axes: var axes })
            return named.TypeKind == TypeKind.Interface || axes.Any(Seed);
        if (UnknownCollection(named))
            return false;
        return named.TypeKind == TypeKind.Class && !named.IsSealed;
    }

    /// <summary>Whether the driver reaches through a field of <paramref name="type"/> to seed fields of library objects stored
    /// below it, or of a struct with references stored in it, whose fields are the holder's storage (R1).</summary>
    /// <param name="type">The field or auto-property type.</param>
    public static bool Path(ITypeSymbol type)
    {
        WorkCancellation.Check();
        if (Struct(type))
            return true;
        if (TypeShape.Of(type) != TypeShapeKind.Reference)
            return false;
        if (type is IArrayTypeSymbol array)
            return Path(array.ElementType);
        if (type is INamedTypeSymbol named && Collection(named) is { Axes: var axes })
            return axes.Any(Path);
        if (type is INamedTypeSymbol unknown && UnknownCollection(unknown))
            return false;
        return type.TypeKind == TypeKind.Class;
    }

    /// <summary>Whether <paramref name="type"/> is a struct with references, a <c>ValueTask</c> included. Its storage is part of its
    /// holder's (R1): a field of it is a path, opened with <c>readonly</c> kept, and setup seeds the struct's fields where it can
    /// write them in place; a struct setup cannot write in place lists its reference fields as unseeded.</summary>
    /// <param name="type">The receiver, argument, field or auto-property type.</param>
    public static bool Struct(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Struct, IsRefLikeType: false, IsUnmanagedType: false } &&
        TypeShape.Of(type) is TypeShapeKind.StructWithReferences or TypeShapeKind.Task or TypeShapeKind.TaskOfT;

    /// <summary>The ADR 0010 collection information for <paramref name="type"/>, or <c>null</c>.</summary>
    /// <param name="type">A named type.</param>
    public static CollectionSeed? Collection(INamedTypeSymbol type) =>
        Collections.TryGetValue(MetadataName(type.OriginalDefinition), out var collection)
            ? new CollectionSeed(collection.Concrete, collection.Insert, collection.Dictionary, type.TypeArguments)
            : null;

    /// <summary>Whether <paramref name="type"/> is collection-shaped but is outside the ADR 0010 table.</summary>
    /// <param name="type">A named type.</param>
    public static bool UnknownCollection(INamedTypeSymbol type) => Collection(type) is null &&
        (type.SpecialType == SpecialType.System_Collections_IEnumerable ||
         type.AllInterfaces.Any(candidate => candidate.SpecialType == SpecialType.System_Collections_IEnumerable));

    private static string MetadataName(INamedTypeSymbol type) =>
        type.ContainingNamespace.IsGlobalNamespace ? type.MetadataName : type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName;

    private static bool HasStructConstraint(ITypeParameterSymbol parameter, HashSet<ITypeParameterSymbol> seen)
    {
        WorkCancellation.Check();
        if (!seen.Add(parameter))
            return false;
        if (parameter.HasValueTypeConstraint || parameter.HasUnmanagedTypeConstraint ||
            parameter.BaseType is { TypeKind: TypeKind.Struct })
        {
            return true;
        }

        return parameter.ConstraintTypes.Any(constraint => TypeShape.Of(constraint) is TypeShapeKind.PlainStruct or TypeShapeKind.StructWithReferences ||
                                                           constraint is ITypeParameterSymbol other && HasStructConstraint(other, seen));
    }
}
