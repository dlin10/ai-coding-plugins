using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels;

public sealed class LibraryModelException(string message) : Exception(message);

public enum ModelLayer { BuiltIn, Project }

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

/// <summary>One member the library model describes: the <see cref="DocumentationCommentId"/> of its original definition, the assemblies it
/// may be declared in with their version ranges, and its effects; a member with none touches nothing.</summary>
public sealed record LibraryModel(string Id, IReadOnlyList<SupportedAssemblyVersion> Assemblies, IReadOnlyList<LibraryEffect> Effects,
                                  ModelLayer Layer = ModelLayer.BuiltIn, bool DeclaredOpaque = false, Version? ResolvedVersion = null);

/// <summary>A type every one of whose members is known without effect when it takes only immutable arguments (R2). With
/// <see cref="IncludesDerived"/> the rule covers every type of the framework that derives from it: one declared in the same
/// assemblies, or in any other <c>System</c> assembly at the framework's range.</summary>
public sealed record ImmutableLibraryType(string Id, IReadOnlyList<SupportedAssemblyVersion> Assemblies, bool IncludesDerived = false);

public enum LibraryMatchKind
{
    Known,
    Opaque,

    /// <summary>A member the library model describes, declared in an assembly of the right name at a version outside its range: an
    /// opaque call, counted apart.</summary>
    OutOfRange
}

/// <summary>A call a library model recognizes: the member's id, its effects, and the assembly it was found in with the range the model
/// supports for that assembly.</summary>
public sealed record LibraryMatch(LibraryMatchKind Kind, string MemberId, IReadOnlyList<LibraryEffect> Effects, AssemblyIdentity Assembly,
                                  SupportedAssemblyVersion Range, ModelLayer Layer = ModelLayer.BuiltIn, bool DeclaredOpaque = false);

/// <summary>The built-in library models of TD-034a: known calls by exact member identity, assembly name and version range,
/// checked as they are loaded (TD-123).</summary>
public sealed class LibraryModels
{
    // The types the recognizers of phases 3-4 own, by metadata name: the collections of ADR 0010, spawn, timers and tasks, the
    // synchronization primitives, Interlocked and Volatile, and the DI locator (IrLowering). Their members match by name in every
    // supported framework (TD-122), and a library model entry for one would describe the same call twice.
    private static readonly HashSet<string> RecognizedTypes = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Concurrent.ConcurrentDictionary`2",
        "System.Collections.Concurrent.ConcurrentQueue`1",
        "System.Collections.Concurrent.ConcurrentStack`1",
        "System.Collections.Concurrent.ConcurrentBag`1",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.Queue`1",
        "System.Collections.Generic.Stack`1",
        "System.Collections.Generic.LinkedList`1",
        "System.Collections.Generic.LinkedListNode`1",
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

    private readonly Dictionary<string, LibraryModel> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LibraryModel[]> _projectMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImmutableLibraryType> _types = new(StringComparer.Ordinal);

    public LibraryModels(IEnumerable<LibraryModel> members, IEnumerable<ImmutableLibraryType> immutableTypes)
    {
        foreach (var member in members)
        {
            Check(member.Id, member.Assemblies);
            var effects = new HashSet<LibraryEffect>();
            foreach (var effect in member.Effects)
            {
                if (string.IsNullOrWhiteSpace(effect.Parameter))
                    throw new LibraryModelException($"{member.Id} declares a {effect.Kind} effect with no parameter name.");
                if (!effects.Add(effect))
                    throw new LibraryModelException($"{member.Id} declares {effect.Kind} of '{effect.Parameter}' twice.");
            }

            if (!_members.TryAdd(member.Id, member))
                throw new LibraryModelException($"{member.Id} is described twice.");
        }

        foreach (var type in immutableTypes)
        {
            Check(type.Id, type.Assemblies);
            if (!_types.TryAdd(type.Id, type))
                throw new LibraryModelException($"{type.Id} is described twice.");
        }

        Members = _members.Values.ToArray();
        ImmutableTypes = _types.Values.ToArray();
    }

    private LibraryModels(LibraryModels builtIn, IEnumerable<LibraryModel> project) : this(builtIn.Members, builtIn.ImmutableTypes)
    {
        var projectMembers = project.ToArray();
        foreach (var group in projectMembers.GroupBy(member => member.Id, StringComparer.Ordinal))
            _projectMembers.Add(group.Key, group.ToArray());
        Members = builtIn.Members.Concat(projectMembers).ToArray();
    }

    internal static LibraryModels WithProject(IEnumerable<LibraryModel> project) => new(BuiltIn, project);

    /// <summary>The built-in library models, read from the seven embedded files once per process.</summary>
    public static LibraryModels BuiltIn { get; } = LoadBuiltIn();

    private static LibraryModels LoadBuiltIn()
    {
        var assembly = typeof(LibraryModels).Assembly;
        var members = new List<LibraryModel>();
        var types = new List<ImmutableLibraryType>();
        foreach (var file in new[] { "system.json", "system-text-json.json", "newtonsoft-json.json", "logging.json",
                                     "http-client.json", "entity-framework.json", "linq.json" })
        {
            using var stream = assembly.GetManifestResourceStream("LibraryModels.BuiltIn." + file) ??
                               throw new LibraryModelException($"Missing built-in library model {file}.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var model = BuiltInModelReader.Read(bytes.ToArray());
            members.AddRange(model.Members);
            types.AddRange(model.ImmutableTypes);
        }
        return new LibraryModels(members, types);
    }

    public IReadOnlyList<LibraryModel> Members { get; }

    public IReadOnlyList<ImmutableLibraryType> ImmutableTypes { get; }

    /// <summary>Whether a recognizer of phases 3-4 owns the type with this metadata name: a call of its members keeps the behaviour
    /// of those phases and is never an unresolved call (R1).</summary>
    public static bool IsRecognizedType(string metadataName) => RecognizedTypes.Contains(metadataName);

    /// <summary>What a library model says about a call of <paramref name="method"/>: known, known but for the version of its assembly,
    /// or null for a member it does not describe. A member it describes by its immutable type only is known when every parameter
    /// is immutable and passed by value or <c>out</c>, and it is a method, a constructor, an operator or a getter.</summary>
    public LibraryMatch? Find(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (DocumentationCommentId.CreateDeclarationId(definition) is not { } id)
            return null;
        if (_projectMembers.TryGetValue(id, out var projectMembers))
        {
            var project = projectMembers.FirstOrDefault(member => member.ResolvedVersion == definition.ContainingAssembly.Identity.Version &&
                member.Assemblies.Any(range => range.AssemblyName == definition.ContainingAssembly.Identity.Name &&
                                               range.Contains(definition.ContainingAssembly.Identity.Version)));
            if (project is not null)
                return Match(project.Assemblies, definition.ContainingAssembly, id, project.Effects, project.Layer, project.DeclaredOpaque);
        }
        if (_members.TryGetValue(id, out var member))
            return Match(member.Assemblies, definition.ContainingAssembly, id, member.Effects, member.Layer, member.DeclaredOpaque);
        if (ImmutableTypeOf(definition.ContainingType) is not { } type || !IsImmutableShape(definition))
            return null;
        return Match(RangeOf(type, definition.ContainingAssembly), definition.ContainingAssembly, id, []);
    }

    /// <summary>The assemblies of built-in models that <paramref name="compilation"/> references at a version outside their range, one per
    /// assembly name: every call of theirs those models describe is an opaque call there (R5). They are the assemblies the models
    /// names, and any other <c>System</c> assembly declaring a type a rule for derived types covers.</summary>
    public IEnumerable<(AssemblyIdentity Assembly, SupportedAssemblyVersion Range)> OutOfRangeReferences(Compilation compilation)
    {
        var named = _members.Values.SelectMany(member => member.Assemblies)
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
    /// primitive, an enum, an immutable type of the built-in models whose type arguments are immutable too, and, inside the members of
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
                                       IReadOnlyList<LibraryEffect> effects, ModelLayer layer, bool declaredOpaque) =>
        Match(RangeOf(ranges, assembly), assembly, id, effects, layer, declaredOpaque);

    private static LibraryMatch? Match(SupportedAssemblyVersion? range, IAssemblySymbol? assembly, string id,
                                       IReadOnlyList<LibraryEffect> effects, ModelLayer layer = ModelLayer.BuiltIn,
                                       bool declaredOpaque = false)
    {
        if (range is null)
            return null;
        var kind = !range.Contains(assembly!.Identity.Version) ? LibraryMatchKind.OutOfRange :
                   declaredOpaque ? LibraryMatchKind.Opaque : LibraryMatchKind.Known;
        return new LibraryMatch(kind, id, effects, assembly!.Identity, range, layer, declaredOpaque);
    }

    private static SupportedAssemblyVersion? RangeOf(IReadOnlyList<SupportedAssemblyVersion> ranges, IAssemblySymbol? assembly) =>
        assembly is null ? null : ranges.FirstOrDefault(range => range.AssemblyName == assembly.Identity.Name &&
                                                              range.Contains(assembly.Identity.Version)) ??
                                    ranges.FirstOrDefault(range => range.AssemblyName == assembly.Identity.Name);

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
            throw new LibraryModelException($"{id} names no assembly.");
        foreach (var range in assemblies)
        {
            if (string.IsNullOrWhiteSpace(range.AssemblyName))
                throw new LibraryModelException($"{id} names an assembly with an empty name.");
            if (range.Minimum >= range.MaximumExclusive)
            {
                throw new LibraryModelException(
                    $"{id} declares {range.AssemblyName} {range.Minimum} up to {range.MaximumExclusive}, whose minimum is not below its exclusive maximum.");
            }
        }

        if (RecognizedTypes.Contains(TypeOf(id)))
            throw new LibraryModelException($"{id} belongs to {TypeOf(id)}, which a recognizer of phases 3-4 already owns.");
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
