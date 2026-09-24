using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibrarySemantics;

public sealed class LibrarySemanticsException(string message) : Exception(message);

/// <summary>What a known call does to one of its arguments, named by the parameter it is bound to (TD-034a).</summary>
public enum LibraryEffectKind
{
    /// <summary>A read of every field of every region reachable from the argument: a deep read.</summary>
    DeepRead,

    /// <summary>A write of every field of the argument's own regions, one level deep.</summary>
    WriteArgument
}

public sealed record LibraryEffect(LibraryEffectKind Kind, string Parameter)
{
    public static LibraryEffect DeepReadOf(string parameter) => new(LibraryEffectKind.DeepRead, parameter);

    public static LibraryEffect WriteOf(string parameter) => new(LibraryEffectKind.WriteArgument, parameter);
}

/// <summary>One member the table describes: the <see cref="DocumentationCommentId"/> of its original definition, the assemblies it
/// may be declared in with their version ranges, and its effects; a member with none touches nothing.</summary>
public sealed record LibraryMember(string Id, IReadOnlyList<SupportedAssemblyVersion> Assemblies, IReadOnlyList<LibraryEffect> Effects);

/// <summary>A type every one of whose members is known without effect when it takes only immutable arguments (R2). With
/// <see cref="IncludesDerived"/> the rule covers every type of the framework that derives from it: one declared in the same
/// assemblies, or in any other <c>System</c> assembly at the framework's range.</summary>
public sealed record ImmutableLibraryType(string Id, IReadOnlyList<SupportedAssemblyVersion> Assemblies, bool IncludesDerived = false);

public enum LibraryMatchKind
{
    Known,

    /// <summary>A member the table describes, declared in an assembly of the right name at a version outside its range: an
    /// opaque call, counted apart.</summary>
    OutOfRange
}

/// <summary>A call the table recognizes: the member's id, its effects, and the assembly it was found in with the range the table
/// supports for that assembly.</summary>
public sealed record LibraryMatch(LibraryMatchKind Kind, string MemberId, IReadOnlyList<LibraryEffect> Effects, AssemblyIdentity Assembly,
                                  SupportedAssemblyVersion Range);

/// <summary>The library semantics table of TD-034a: known calls by exact member identity, assembly name and version range, built
/// from its families and checked as it is built (TD-123).</summary>
public sealed class LibrarySemanticsTable
{
    // The types the recognizers of phases 3-4 own, by metadata name: the collections of ADR 0010, spawn, timers and tasks, the
    // synchronization primitives, Interlocked and Volatile, and the DI locator (IrLowering). Their members match by name in every
    // supported framework (TD-122), and a table entry for one would describe the same call twice.
    private static readonly HashSet<string> RecognizedTypes = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Concurrent.ConcurrentDictionary`2",
        "System.Collections.Concurrent.ConcurrentQueue`1",
        "System.Collections.Concurrent.ConcurrentStack`1",
        "System.Collections.Concurrent.ConcurrentBag`1",
        "System.Threading.Tasks.Task",
        "System.Threading.Tasks.Task`1",
        "System.Threading.Tasks.TaskFactory",
        "System.Threading.Tasks.TaskFactory`1",
        "System.Threading.Tasks.TaskExtensions",
        "System.Threading.Tasks.Parallel",
        "System.Threading.Tasks.ValueTask",
        "System.Threading.Tasks.ValueTask`1",
        "System.Threading.ThreadPool",
        "System.Threading.Thread",
        "System.Threading.Timer",
        "System.Threading.PeriodicTimer",
        "System.Threading.WaitHandle",
        "System.Timers.Timer",
        "System.Threading.Monitor",
        "System.Threading.Lock",
        "System.Threading.Mutex",
        "System.Threading.SemaphoreSlim",
        "System.Threading.ReaderWriterLockSlim",
        "System.Threading.Interlocked",
        "System.Threading.Volatile",
        "System.IServiceProvider",
        "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions",
        "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory",
        "Microsoft.Extensions.DependencyInjection.IServiceScope",
        "Microsoft.Extensions.DependencyInjection.AsyncServiceScope"
    };

    private readonly Dictionary<string, LibraryMember> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImmutableLibraryType> _types = new(StringComparer.Ordinal);

    public LibrarySemanticsTable(IEnumerable<LibraryMember> members, IEnumerable<ImmutableLibraryType> immutableTypes)
    {
        foreach (var member in members)
        {
            Check(member.Id, member.Assemblies);
            var effects = new HashSet<LibraryEffect>();
            foreach (var effect in member.Effects)
            {
                if (string.IsNullOrWhiteSpace(effect.Parameter))
                    throw new LibrarySemanticsException($"{member.Id} declares a {effect.Kind} effect with no parameter name.");
                if (!effects.Add(effect))
                    throw new LibrarySemanticsException($"{member.Id} declares {effect.Kind} of '{effect.Parameter}' twice.");
            }

            if (!_members.TryAdd(member.Id, member))
                throw new LibrarySemanticsException($"{member.Id} is described twice.");
        }

        foreach (var type in immutableTypes)
        {
            Check(type.Id, type.Assemblies);
            if (!_types.TryAdd(type.Id, type))
                throw new LibrarySemanticsException($"{type.Id} is described twice.");
        }

        Members = _members.Values.ToArray();
        ImmutableTypes = _types.Values.ToArray();
    }

    /// <summary>The table of phase 5a; its families are listed here and nowhere else.</summary>
    public static LibrarySemanticsTable BuiltIn { get; } = new(
        [
            .. SystemFamily.Members,
            .. SystemTextJsonFamily.Members,
            .. NewtonsoftJsonFamily.Members,
            .. LoggingFamily.Members,
            .. HttpClientFamily.Members,
            .. EntityFrameworkFamily.Members,
            .. LinqFamily.Members
        ],
        SystemFamily.ImmutableTypes);

    public IReadOnlyList<LibraryMember> Members { get; }

    public IReadOnlyList<ImmutableLibraryType> ImmutableTypes { get; }

    /// <summary>What the table says about a call of <paramref name="method"/>: known, known but for the version of its assembly,
    /// or null for a member it does not describe. A member it describes by its immutable type only is known when every parameter
    /// is immutable and passed by value or <c>out</c>, and it is a method, a constructor, an operator or a getter.</summary>
    public LibraryMatch? Find(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (DocumentationCommentId.CreateDeclarationId(definition) is not { } id)
            return null;
        if (_members.TryGetValue(id, out var member))
            return Match(member.Assemblies, definition.ContainingAssembly, id, member.Effects);
        if (ImmutableTypeOf(definition.ContainingType) is not { } type || !IsImmutableShape(definition))
            return null;
        return Match(RangeOf(type, definition.ContainingAssembly), definition.ContainingAssembly, id, []);
    }

    /// <summary>The assemblies of the table that <paramref name="compilation"/> references at a version outside their range, one per
    /// assembly name: every call of theirs the table describes is an opaque call there (R5). They are the assemblies the table
    /// names, and any other <c>System</c> assembly declaring a type a rule for derived types covers.</summary>
    public IEnumerable<(AssemblyIdentity Assembly, SupportedAssemblyVersion Range)> OutOfRangeReferences(Compilation compilation)
    {
        var named = Members.SelectMany(member => member.Assemblies)
                           .Concat(ImmutableTypes.SelectMany(type => type.Assemblies))
                           .DistinctBy(range => range.AssemblyName, StringComparer.Ordinal)
                           .ToArray();
        var derived = compilation.SourceModule.ReferencedAssemblySymbols
                                 .Where(assembly => named.All(range => range.AssemblyName != assembly.Identity.Name))
                                 .SelectMany(assembly => ImmutableTypes.Where(type => type.IncludesDerived)
                                                                       .Select(type => (Type: type, Assembly: assembly, Range: RangeOf(type, assembly))))
                                 .Where(entry => entry.Range is { } range && !range.Contains(entry.Assembly.Identity.Version) &&
                                                 Declares(entry.Assembly.GlobalNamespace, entry.Type.Id))
                                 .Select(entry => (entry.Assembly.Identity, entry.Range!));
        return named.Select(range => (Assembly: ExactSymbols.FindReferencedAssembly(compilation, range.AssemblyName), Range: range))
                    .Where(pair => pair.Assembly is not null && !pair.Range.Contains(pair.Assembly.Identity.Version))
                    .Select(pair => (pair.Assembly!.Identity, pair.Range))
                    .Concat(derived)
                    .DistinctBy(pair => pair.Item1.Name, StringComparer.Ordinal);
    }

    /// <summary>Whether a namespace of an assembly declares a type deriving from the type with the documentation id
    /// <paramref name="baseId"/>.</summary>
    private static bool Declares(INamespaceSymbol @namespace, string baseId) =>
        @namespace.GetTypeMembers().Any(type => DerivesFrom(type, baseId)) ||
        @namespace.GetNamespaceMembers().Any(nested => Declares(nested, baseId));

    private static bool DerivesFrom(INamedTypeSymbol type, string baseId)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (DocumentationCommentId.CreateDeclarationId(current.OriginalDefinition) == baseId)
                return true;
        }

        return false;
    }

    /// <summary>Whether a value of <paramref name="type"/> can be neither changed nor used to reach anything that can: a string, a
    /// primitive, an enum, an immutable type of the table whose type arguments are immutable too, and, inside the members of
    /// <paramref name="owner"/>, one of its own type parameters.</summary>
    public bool IsImmutable(ITypeSymbol type, INamedTypeSymbol? owner = null) => IsImmutable(type, owner, inRange: true);

    /// <summary><see cref="IsImmutable(ITypeSymbol, INamedTypeSymbol?)"/>, with the version of the type's assembly checked only
    /// when <paramref name="inRange"/>: a member's shape is the same at every version, and whether its own assembly is in range is
    /// what tells known from out of range (R5).</summary>
    private bool IsImmutable(ITypeSymbol type, INamedTypeSymbol? owner, bool inRange) => type switch
    {
        { TypeKind: TypeKind.Enum } => true,
        ITypeParameterSymbol parameter => owner is not null && parameter.TypeParameterKind == TypeParameterKind.Type &&
                                          SymbolEqualityComparer.Default.Equals(parameter.DeclaringType, owner.OriginalDefinition),
        INamedTypeSymbol named => ImmutableTypeOf(named) is { } entry &&
                                  RangeOf(entry, named.ContainingAssembly) is { } range &&
                                  (!inRange || range.Contains(named.ContainingAssembly.Identity.Version)) &&
                                  named.TypeArguments.All(argument => IsImmutable(argument, owner, inRange)),
        _ => false
    };

    private ImmutableLibraryType? ImmutableTypeOf(INamedTypeSymbol? type)
    {
        if (type?.OriginalDefinition is not { } definition)
            return null;
        if (DocumentationCommentId.CreateDeclarationId(definition) is { } id && _types.TryGetValue(id, out var entry))
            return entry;
        for (var current = definition.BaseType; current is not null; current = current.BaseType)
        {
            if (DocumentationCommentId.CreateDeclarationId(current.OriginalDefinition) is { } baseId &&
                _types.TryGetValue(baseId, out var baseEntry) && baseEntry.IncludesDerived &&
                RangeOf(baseEntry, definition.ContainingAssembly) is not null)
            {
                return baseEntry;
            }
        }

        return null;
    }

    private bool IsImmutableShape(IMethodSymbol method) =>
        method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.PropertyGet or MethodKind.UserDefinedOperator
                             or MethodKind.Conversion &&
        method.Parameters.All(parameter => parameter.RefKind is RefKind.None or RefKind.Out &&
                                           IsImmutable(parameter.Type, method.ContainingType, inRange: false));

    private static LibraryMatch? Match(IReadOnlyList<SupportedAssemblyVersion> ranges, IAssemblySymbol? assembly, string id,
                                       IReadOnlyList<LibraryEffect> effects) =>
        Match(RangeOf(ranges, assembly), assembly, id, effects);

    private static LibraryMatch? Match(SupportedAssemblyVersion? range, IAssemblySymbol? assembly, string id,
                                       IReadOnlyList<LibraryEffect> effects)
    {
        if (range is null)
            return null;
        var kind = range.Contains(assembly!.Identity.Version) ? LibraryMatchKind.Known : LibraryMatchKind.OutOfRange;
        return new LibraryMatch(kind, id, effects, assembly.Identity, range);
    }

    private static SupportedAssemblyVersion? RangeOf(IReadOnlyList<SupportedAssemblyVersion> ranges, IAssemblySymbol? assembly) =>
        assembly is null ? null : ranges.FirstOrDefault(range => range.AssemblyName == assembly.Identity.Name);

    /// <summary>The range an immutable type's rule holds for in <paramref name="assembly"/>: one of its own assemblies, or, for a
    /// rule that covers derived types, any other <c>System</c> assembly of the framework at the framework's range.</summary>
    private static SupportedAssemblyVersion? RangeOf(ImmutableLibraryType type, IAssemblySymbol? assembly) =>
        RangeOf(type.Assemblies, assembly) ??
        (type.IncludesDerived && assembly?.Identity.Name is { } name && (name == "System" || name.StartsWith("System.", StringComparison.Ordinal))
            ? SupportedAssemblyVersion.Framework(name)
            : null);

    private static void Check(string id, IReadOnlyList<SupportedAssemblyVersion> assemblies)
    {
        if (assemblies.Count == 0)
            throw new LibrarySemanticsException($"{id} names no assembly.");
        foreach (var range in assemblies)
        {
            if (string.IsNullOrWhiteSpace(range.AssemblyName))
                throw new LibrarySemanticsException($"{id} names an assembly with an empty name.");
            if (range.Minimum >= range.MaximumExclusive)
            {
                throw new LibrarySemanticsException(
                    $"{id} declares {range.AssemblyName} {range.Minimum} up to {range.MaximumExclusive}, whose minimum is not below its exclusive maximum.");
            }
        }

        if (RecognizedTypes.Contains(TypeOf(id)))
            throw new LibrarySemanticsException($"{id} belongs to {TypeOf(id)}, which a recognizer of phases 3-4 already owns.");
    }

    /// <summary>The metadata name of the type an id names or declares a member of: <c>T:System.String</c> names
    /// <c>System.String</c>, <c>M:System.Linq.Enumerable.ToList``1(...)</c> is declared by <c>System.Linq.Enumerable</c>.</summary>
    private static string TypeOf(string id)
    {
        var name = id[2..];
        if (id.StartsWith("T:", StringComparison.Ordinal))
            return name;
        var signature = name.IndexOfAny(['(', '~']);
        var qualified = signature < 0 ? name : name[..signature];
        return qualified[..qualified.LastIndexOf('.')];
    }
}
