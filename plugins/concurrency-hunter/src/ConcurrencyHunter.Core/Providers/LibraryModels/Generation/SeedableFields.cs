using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The one owner of whether a library field can receive a seed, is a path to further fields, or is a struct whose
/// reference fields cannot be opened. Each answer starts with <see cref="TypeShape.Of"/>.</summary>
public static class SeedableFields
{
    /// <summary>The static storage loaded by the closure reached from a driver, keyed by its declaring type definition and name.</summary>
    /// <param name="run">The driver's pipeline run.</param>
    public static IReadOnlySet<(string Type, string Name)> LoadedStatics(ScopeRun run)
    {
        var fields = run.Reachable.Bodies.Values.SelectMany(body => body.Blocks).SelectMany(block => block.Operations)
                        .OfType<IrLoadFieldOperation>().Select(load => load.Field)
                        .Concat(run.Executions?.Accesses.Where(access => access.Access.Kind == SummaryAccessKind.Load)
                                   .Select(access => access.Access.Field) ?? [])
                        .Concat(ReferenceLoads(run));
        return fields.Where(field => field.IsStatic)
                     .Select(field => (run.Program.Decompose($"{field.Assembly}:{field.ContainingTypeId}").DefinitionKey, field.Name))
                     .ToHashSet();
    }

    /// <summary>The static storage a field or auto-property declares, in the same form as <see cref="LoadedStatics"/>.</summary>
    /// <param name="member">The field or auto-property.</param>
    public static (string Type, string Name) StaticStorage(ISymbol member) =>
        (SymbolNames.TypeKey(member.ContainingType.OriginalDefinition), member.Name);

    private static IEnumerable<IrFieldRef> ReferenceLoads(ScopeRun run)
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
            var (instance, target) = item;
            if (!seen.Add((instance.Id, target)))
                continue;
            switch (target)
            {
                case ReferenceCell cell:
                    yield return cell.Field;
                    break;
                case ReferenceCall call:
                    foreach (var edge in outgoing[(instance.Id, call.OperationId)])
                    {
                        var callee = heap.Instances[edge.CalleeInstance];
                        foreach (var returned in callee.Summary.ReferenceReturns)
                            pending.Enqueue((callee, returned));
                    }
                    break;
                case ReferenceParameter parameter:
                    foreach (var edge in incoming[instance.Id])
                    {
                        var caller = heap.Instances[edge.CallerInstance];
                        var arguments = caller.Summary.Calls.Where(call => call.OperationId == edge.OperationId)
                                              .SelectMany(call => call.Arguments)
                                              .Where(argument => argument.ParameterOrdinal == parameter.Ordinal);
                        foreach (var reference in arguments.SelectMany(argument => argument.References))
                            pending.Enqueue((caller, reference));
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
    /// below it.</summary>
    /// <param name="type">The field or auto-property type.</param>
    public static bool Path(ITypeSymbol type)
    {
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

    /// <summary>Whether <paramref name="type"/> is a struct with references. Its containing field is neither seeded nor opened;
    /// reference fields inside the struct are recorded as unreachable instead.</summary>
    /// <param name="type">The field or auto-property type.</param>
    public static bool Struct(ITypeSymbol type) => TypeShape.Of(type) == TypeShapeKind.StructWithReferences;

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
