using System.Text;
using System.Text.RegularExpressions;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.CallGraph;

public enum ProgramMethodKind
{
    Ordinary,
    Constructor,
    TypeInitializer,
    Accessor,
    Operator
}

/// <summary>A type of the scope, keyed by its original definition's type key. <see cref="BaseTypeKey"/> and
/// <see cref="InterfaceKeys"/> are constructed as the declaration writes them (<c>Base&lt;Order&gt;</c>), in terms of
/// <see cref="TypeParameterKeys"/>, which list the type parameters of the containing types first.</summary>
public sealed record ProgramType(string TypeKey, string DisplayName, string Assembly, string? BaseTypeKey,
                                 IReadOnlyList<string> InterfaceKeys, bool IsInterface, bool IsAbstract, bool IsSealed,
                                 bool IsDelegate, bool IsValueType, IReadOnlyList<string> TypeParameterKeys)
{
    /// <summary>The instance fields a source type declares itself, as an access names them: its fields, the backing fields of its
    /// automatic properties and its captured primary constructor parameters. Empty for a type from metadata.</summary>
    public IReadOnlyList<IrFieldRef> InstanceFields { get; init; } = [];

    /// <summary>Whether the type is declared in source, so its instance fields are all known.</summary>
    public bool IsSource { get; init; }
}

public sealed record ProgramParameter(string Name, string TypeKey, IrRefKind RefKind);

/// <summary>A method in body-id form. <see cref="NestedBodyIds"/> are the ids of its lambdas and local functions, listed only when
/// it has a source body; <see cref="TypeParameterKeys"/> are the method's own type parameters.</summary>
public sealed record ProgramMethod(string MethodId, string Name, string DisplaySymbol, string ContainingTypeKey, ProgramMethodKind Kind,
                                   bool IsStatic, bool IsAbstract, bool IsVirtual, bool IsOverride, string? OverriddenMethodId,
                                   IReadOnlyList<string> ImplementedInterfaceMethodIds, IReadOnlyList<ProgramParameter> Parameters,
                                   string ReturnTypeKey, bool HasSourceBody, IReadOnlyList<string> NestedBodyIds,
                                   IReadOnlyList<string> TypeParameterKeys);

public sealed record ProgramField(string ContainingTypeKey, string Name, bool IsStatic, bool IsReadOnly, string FieldTypeKey);

/// <summary>A closed generic type source mentions; <see cref="TypeArgumentKeys"/> follow the definition's
/// <see cref="ProgramType.TypeParameterKeys"/>.</summary>
public sealed record ClosedGenericType(string TypeKey, string OriginalDefinitionKey, IReadOnlyList<string> TypeArgumentKeys);

/// <summary>The types, methods and fields of one process scope, with the lookups call resolution needs. A type key that is not
/// a known closed generic type resolves to its definition by its shape: the same name with the same number of type
/// arguments.</summary>
public sealed class ProgramIndex
{
    private readonly Dictionary<string, ProgramType> _types;
    private readonly Dictionary<string, ProgramMethod> _methods;
    private readonly Dictionary<string, ClosedGenericType> _closedTypes;
    private readonly Dictionary<string, ProgramMethod[]> _methodsByType;
    private readonly Dictionary<string, string> _definitionsByShape;
    private readonly HashSet<string> _typeParameterKeys;

    public ProgramIndex(string scopeId, IReadOnlyList<ProgramType> types, IReadOnlyList<ProgramMethod> methods,
                        IReadOnlyList<ProgramField> fields, IReadOnlyList<ClosedGenericType> closedGenericTypes)
    {
        ScopeId = scopeId;
        Types = types;
        Methods = methods;
        Fields = fields;
        ClosedGenericTypes = closedGenericTypes;
        _types = types.ToDictionary(type => type.TypeKey, StringComparer.Ordinal);
        _methods = methods.ToDictionary(method => method.MethodId, StringComparer.Ordinal);
        _closedTypes = closedGenericTypes.ToDictionary(type => type.TypeKey, StringComparer.Ordinal);
        _methodsByType = methods.GroupBy(method => method.ContainingTypeKey, StringComparer.Ordinal)
                                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        _definitionsByShape = types.Where(type => type.TypeParameterKeys.Count != 0)
                                   .GroupBy(type => Shape(type.TypeKey).Shape, StringComparer.Ordinal)
                                   .ToDictionary(group => group.Key, group => group.First().TypeKey, StringComparer.Ordinal);
        _typeParameterKeys = types.SelectMany(type => type.TypeParameterKeys)
                                  .Concat(methods.SelectMany(method => method.TypeParameterKeys))
                                  .ToHashSet(StringComparer.Ordinal);
    }

    public string ScopeId { get; }
    public IReadOnlyList<ProgramType> Types { get; }
    public IReadOnlyList<ProgramMethod> Methods { get; }
    public IReadOnlyList<ProgramField> Fields { get; }
    public IReadOnlyList<ClosedGenericType> ClosedGenericTypes { get; }

    /// <summary>The type keys source creates objects of whose values can be neither changed nor used to reach anything that can
    /// (the library table's immutable types): a known call's effect on such an object touches nothing (R3).</summary>
    public IReadOnlySet<string> ImmutableTypeKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public ProgramMethod? Method(string methodId) => _methods.GetValueOrDefault(methodId);

    /// <summary>The instance fields of an object of <paramref name="typeKey"/>, its own and those of its source base types, each
    /// named for the closed type that holds it; null when the type itself is not declared in source. A base from metadata ends
    /// the walk: its state is a library object's, which is no resource.</summary>
    public IReadOnlyList<IrFieldRef>? InstanceFieldsOf(string typeKey)
    {
        var fields = new List<IrFieldRef>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (string? current = typeKey; current is not null;)
        {
            var (definitionKey, arguments) = Decompose(current);
            if (!visited.Add(definitionKey) || !_types.TryGetValue(definitionKey, out var type) || !type.IsSource)
                return fields.Count == 0 && visited.Count == 1 ? null : fields;
            var substitution = type.TypeParameterKeys.Zip(arguments).Where(pair => pair.First != pair.Second)
                                   .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            fields.AddRange(type.InstanceFields.Select(field => field.ContainingTypeIdentity is { } identity && substitution.Count != 0
                                                                    ? field with { ContainingTypeIdentity = Substitute(identity, substitution) }
                                                                    : field));
            current = type.BaseTypeKey is { } baseKey ? Substitute(baseKey, substitution) : null;
        }

        return fields;
    }

    /// <summary>The type <paramref name="typeKey"/> is an instance of, through its original definition.</summary>
    public ProgramType? Type(string typeKey) => _types.GetValueOrDefault(Decompose(typeKey).DefinitionKey);

    /// <summary>The methods a type declares, through its original definition.</summary>
    public IReadOnlyList<ProgramMethod> MethodsOf(string typeKey) =>
        _methodsByType.GetValueOrDefault(Decompose(typeKey).DefinitionKey) ?? [];

    public IReadOnlyList<ProgramMethod> Constructors(string typeKey) =>
        MethodsOf(typeKey).Where(method => method.Kind == ProgramMethodKind.Constructor).ToArray();

    /// <summary>The method an instance of <paramref name="runtimeTypeKey"/> runs for a call of <paramref name="methodId"/>: the most
    /// derived override or interface implementation along its base chain, the method itself when it has a body, or null.</summary>
    public ProgramMethod? Implementation(string runtimeTypeKey, string methodId)
    {
        if (!_methods.TryGetValue(methodId, out var target))
            return null;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var current = Decompose(runtimeTypeKey).DefinitionKey;
             visited.Add(current) && _types.TryGetValue(current, out var type);
             current = type.BaseTypeKey is null ? "" : Decompose(type.BaseTypeKey).DefinitionKey)
        {
            foreach (var method in _methodsByType.GetValueOrDefault(current) ?? [])
            {
                if (method.MethodId == methodId && !method.IsAbstract || Overrides(method, methodId) ||
                    method.ImplementedInterfaceMethodIds.Contains(methodId, StringComparer.Ordinal))
                {
                    return method;
                }
            }
        }

        return target.IsAbstract ? null : target;
    }

    /// <summary>Whether a method can be the target of a delegate of <paramref name="delegateTypeKey"/>: the same number of
    /// parameters with the same ref kinds, and a return value exactly when the delegate has one.</summary>
    public bool IsDelegateCompatible(string delegateTypeKey, string methodId)
    {
        var invoke = Type(delegateTypeKey) is { IsDelegate: true }
            ? MethodsOf(delegateTypeKey).FirstOrDefault(method => method.Name == "Invoke")
            : null;
        if (invoke is null || !_methods.TryGetValue(methodId, out var method))
            return false;

        return invoke.Parameters.Count == method.Parameters.Count &&
               invoke.Parameters.Zip(method.Parameters).All(pair => pair.First.RefKind == pair.Second.RefKind) &&
               IsVoid(invoke.ReturnTypeKey) == IsVoid(method.ReturnTypeKey);
    }

    /// <summary>The form <paramref name="typeKey"/> gives the base type or interface whose original definition is
    /// <paramref name="originalDefinitionKey"/>, found through the whole base and interface graph with each level's type
    /// arguments substituted (for <c>Derived : Base&lt;Order&gt;</c>, <c>Base&lt;T&gt;</c> is <c>Base&lt;Order&gt;</c>), or null.</summary>
    public string? ConstructedBase(string typeKey, string originalDefinitionKey)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>([typeKey]);
        while (pending.TryDequeue(out var key))
        {
            if (!visited.Add(key))
                continue;

            var (definitionKey, arguments) = Decompose(key);
            if (definitionKey == originalDefinitionKey)
                return key;
            if (!_types.TryGetValue(definitionKey, out var type) || type.TypeParameterKeys.Count != arguments.Count)
                continue;

            var substitution = type.TypeParameterKeys.Zip(arguments).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            foreach (var baseKey in type.InterfaceKeys.Prepend(type.BaseTypeKey).OfType<string>())
                pending.Enqueue(Substitute(baseKey, substitution));
        }

        return null;
    }

    /// <summary>Whether a type key still names a type parameter of a type or method of the scope.</summary>
    public bool IsOpen(string typeKey) =>
        typeKey.Split(['<', '>', ',', ' ', '[', ']', '*'], StringSplitOptions.RemoveEmptyEntries).Any(_typeParameterKeys.Contains);

    /// <summary>The original definition a type key is an instance of, with its type arguments in the order of the definition's
    /// <see cref="ProgramType.TypeParameterKeys"/>; a definition key decomposes to its own type parameters.</summary>
    public (string DefinitionKey, IReadOnlyList<string> Arguments) Decompose(string typeKey)
    {
        if (_closedTypes.TryGetValue(typeKey, out var closed))
            return (closed.OriginalDefinitionKey, closed.TypeArgumentKeys);
        if (_types.TryGetValue(typeKey, out var type))
            return (typeKey, type.TypeParameterKeys);

        var (shape, arguments) = Shape(typeKey);
        return _definitionsByShape.TryGetValue(shape, out var definition) ? (definition, arguments) : (typeKey, []);
    }

    private bool Overrides(ProgramMethod method, string methodId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var overridden = method.OverriddenMethodId; overridden is not null && visited.Add(overridden);
             overridden = _methods.GetValueOrDefault(overridden)?.OverriddenMethodId)
        {
            if (overridden == methodId)
                return true;
        }

        return false;
    }

    private static bool IsVoid(string typeKey) => typeKey.EndsWith(":void", StringComparison.Ordinal);

    /// <summary>A type key with every outermost type-argument list replaced by its argument count, and those arguments in
    /// order, containing types first.</summary>
    private static (string Shape, IReadOnlyList<string> Arguments) Shape(string typeKey)
    {
        var shape = new StringBuilder();
        var arguments = new List<string>();
        var depth = 0;
        var start = 0;
        var count = 0;
        for (var index = 0; index < typeKey.Length; index++)
        {
            var character = typeKey[index];
            if (character == '<')
            {
                if (depth++ == 0)
                    (start, count) = (index + 1, 1);
            }
            else if (character == '>')
            {
                if (--depth == 0)
                {
                    arguments.Add(typeKey[start..index].Trim());
                    shape.Append('<').Append(count).Append('>');
                }
            }
            else if (character == ',' && depth == 1)
            {
                arguments.Add(typeKey[start..index].Trim());
                (start, count) = (index + 1, count + 1);
            }
            else if (depth == 0)
            {
                shape.Append(character);
            }
        }

        return (shape.ToString(), arguments);
    }

    /// <summary>Replaces whole type-parameter keys, never a key that merely contains one.</summary>
    public static string Substitute(string typeKey, IReadOnlyDictionary<string, string> substitution)
    {
        if (substitution.Count == 0)
            return typeKey;
        var alternatives = string.Join("|", substitution.Keys.OrderByDescending(key => key.Length).Select(Regex.Escape));
        return Regex.Replace(typeKey, $@"(?<=^|[<\s,])(?:{alternatives})(?=$|[,>\[*])", match => substitution[match.Value]);
    }
}
