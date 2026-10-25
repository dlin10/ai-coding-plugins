using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConcurrencyHunter.Frontend;

/// <summary>Builds the program index of a process scope: every source type of its compilations and every metadata type a source
/// type derives from or implements, their methods and fields, and the closed generic types source mentions. What one compilation
/// alone decides — the types its syntax names and the nested bodies of its methods — is computed once per compilation and reused
/// by every index built over it: a project in several scopes, and the library of every member the model generator is asked for,
/// is walked once.</summary>
public static class ProgramIndexBuilder
{
    /// <summary>The types each compilation's syntax names, in the order its walk meets them.</summary>
    private static readonly ConditionalWeakTable<Compilation, MentionedType[]> Mentions = new();

    /// <summary>The nested body ids of each source method, under the compilation that declares it.</summary>
    private static readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<IMethodSymbol, IReadOnlyList<string>>> NestedBodies = new();

    /// <summary>A type a compilation's syntax names: a generic name's type, a type argument, the type of a <c>new</c> or a
    /// <c>typeof</c>, with what the index does with it.</summary>
    /// <param name="Type">The type.</param>
    /// <param name="Immutable">Whether the index checks it for an immutable type source names.</param>
    /// <param name="Closed">Whether the index adds it as a closed generic type source mentions.</param>
    private readonly record struct MentionedType(ITypeSymbol Type, bool Immutable, bool Closed);

    public static ProgramIndex Build(string scopeId, IReadOnlyList<Compilation> compilations, string rootDirectory,
                                     CancellationToken cancellationToken)
    {
        // Source types first, each from its own compilation, so a base type declared in another project of the scope is that
        // project's symbol.
        var sourceTypes = compilations.SelectMany(compilation => SourceTypes(compilation.Assembly.GlobalNamespace)).ToArray();
        var types = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        foreach (var type in sourceTypes)
        {
            WorkCancellation.Check();
            types.TryAdd(SymbolNames.TypeKey(type), type);
        }

        var sourceCompilations = new Dictionary<INamedTypeSymbol, Compilation>(SymbolEqualityComparer.Default);
        foreach (var compilation in compilations)
        {
            WorkCancellation.Check();
            foreach (var type in SourceTypes(compilation.Assembly.GlobalNamespace))
            {
                WorkCancellation.Check();
                sourceCompilations.TryAdd(type, compilation);
            }
        }

        var closedTypes = new Dictionary<string, ClosedGenericType>(StringComparer.Ordinal);
        var symbols = types.ToDictionary(pair => pair.Key, pair => (ITypeSymbol)pair.Value, StringComparer.Ordinal);
        var variantTypes = new Dictionary<string, ProgramVariantType>(StringComparer.Ordinal);
        foreach (var type in sourceTypes)
        {
            WorkCancellation.Check();
            cancellationToken.ThrowIfCancellationRequested();
            for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                WorkCancellation.Check();
                types.TryAdd(SymbolNames.TypeKey(baseType.OriginalDefinition), baseType.OriginalDefinition);
            }
            foreach (var @interface in type.AllInterfaces)
            {
                WorkCancellation.Check();
                types.TryAdd(SymbolNames.TypeKey(@interface.OriginalDefinition), @interface.OriginalDefinition);
            }
            AddMentionedTypes(type, closedTypes, variantTypes);
        }

        // The objects of an immutable type source names are regions a known call's effect touches nothing of (R3). Source names
        // the type wherever such an object comes from: the `new` that makes it, the type argument or `typeof` a container is
        // given it by, and the field, property or parameter that takes it in.
        var immutableTypeKeys = new HashSet<string>(StringComparer.Ordinal);
        void AddImmutable(ITypeSymbol? type)
        {
            if (type is not null && LibraryModels.BuiltIn.IsImmutable(type))
                immutableTypeKeys.Add(SymbolNames.TypeKey(type));
        }

        foreach (var compilation in compilations)
        {
            WorkCancellation.Check();
            foreach (var mentioned in Mentions.GetValue(compilation, key => Mentioned(key, cancellationToken)))
            {
                WorkCancellation.Check();
                symbols.TryAdd(SymbolNames.TypeKey(mentioned.Type), mentioned.Type);
                if (mentioned.Immutable)
                    AddImmutable(mentioned.Type);
                if (mentioned.Closed)
                    AddClosed(mentioned.Type, closedTypes, variantTypes);
            }
        }

        foreach (var member in sourceTypes.SelectMany(type => type.GetMembers()))
        {
            WorkCancellation.Check();
            switch (member)
            {
                case IFieldSymbol field:
                    AddImmutable(field.Type);
                    break;
                case IPropertySymbol property:
                    AddImmutable(property.Type);
                    break;
                case IMethodSymbol method:
                    foreach (var parameter in method.Parameters)
                    {
                        WorkCancellation.Check();
                        AddImmutable(parameter.Type);
                    }
                    break;
            }
        }

        var implementations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var interfaceMappings = new List<ProgramInterfaceMapping>();
        foreach (var type in sourceTypes)
        {
            WorkCancellation.Check();
            var typeKey = SymbolNames.TypeKey(type);
            foreach (var @interface in type.AllInterfaces)
            {
                WorkCancellation.Check();
                var interfaceKey = SymbolNames.TypeKey(@interface);
                foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>())
                {
                    WorkCancellation.Check();
                    var implementation = type.FindImplementationForInterfaceMember(member) as IMethodSymbol;
                    interfaceMappings.Add(new ProgramInterfaceMapping(typeKey, interfaceKey,
                        IrLowering.RootBodyId(member.OriginalDefinition),
                        implementation is null ? null : IrLowering.RootBodyId(implementation.OriginalDefinition),
                        TypeArguments(@interface.OriginalDefinition).Select(parameter => parameter is ITypeParameterSymbol typeParameter
                            ? typeParameter.Variance switch
                            {
                                VarianceKind.Out => ProgramVariance.Covariant,
                                VarianceKind.In => ProgramVariance.Contravariant,
                                _ => ProgramVariance.Invariant
                            }
                            : ProgramVariance.Invariant).ToArray()));
                }
            }
        }
        foreach (var type in types.Values.Where(type => type.TypeKind != TypeKind.Interface))
        {
            WorkCancellation.Check();
            foreach (var @interface in type.AllInterfaces)
            {
                WorkCancellation.Check();
                foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>())
                {
                    WorkCancellation.Check();
                    if (type.FindImplementationForInterfaceMember(member) is not IMethodSymbol implementation)
                        continue;
                    var implementationId = IrLowering.RootBodyId(implementation.OriginalDefinition);
                    if (!implementations.TryGetValue(implementationId, out var implemented))
                        implementations.Add(implementationId, implemented = []);
                    var memberId = IrLowering.RootBodyId(member.OriginalDefinition);
                    if (!implemented.Contains(memberId, StringComparer.Ordinal))
                        implemented.Add(memberId);
                }
            }
        }

        var methods = new Dictionary<string, ProgramMethod>(StringComparer.Ordinal);
        var fields = new List<ProgramField>();
        foreach (var (typeKey, type) in types)
        {
            WorkCancellation.Check();
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                WorkCancellation.Check();
                cancellationToken.ThrowIfCancellationRequested();
                var methodId = IrLowering.RootBodyId(method);
                if (!methods.ContainsKey(methodId))
                    methods.Add(methodId, Method(method, typeKey, implementations, compilations, cancellationToken));
            }

            fields.AddRange(type.GetMembers().OfType<IFieldSymbol>()
                                .Where(field => !field.IsImplicitlyDeclared)
                                .Select(field => new ProgramField(typeKey, field.Name, field.IsStatic, LibraryFieldOpening.DeclaredReadOnly(field),
                                                                  SymbolNames.TypeKey(field.Type))));
            // A field-like event is the delegate field the compiler declares for it, which Roslyn does not list (ADR 0014).
            fields.AddRange(type.GetMembers().OfType<IEventSymbol>()
                                .Where(FieldLikeEvents.Is)
                                .Select(@event => new ProgramField(typeKey, @event.Name, @event.IsStatic, false,
                                                                   SymbolNames.TypeKey(@event.Type))));
        }

        foreach (var (key, type) in types)
        {
            WorkCancellation.Check();
            symbols.TryAdd(key, type);
        }

        return new ProgramIndex(scopeId,
                                types.Select(pair => Type(pair.Key, pair.Value, sourceCompilations.GetValueOrDefault(pair.Value), cancellationToken))
                                     .ToArray(),
                                methods.Values.ToArray(), fields, closedTypes.Values.ToArray(), interfaceMappings,
                                variantTypes.Values.ToArray())
        {
            ImmutableTypeKeys = immutableTypeKeys,
            ImplementsSelfInterface = SelfInterfaceConversions(compilations, symbols)
        };
    }

    /// <summary>The compiler's answer to CoreLib's assignability test, including variance inside the comparable argument. The callback
    /// is shared by reachability and heap selection through <see cref="DefaultComparers"/>. Constructed bindings not written in syntax
    /// are reconstructed from their known definitions and arguments, arrays of any rank through <c>CreateArrayTypeSymbol</c>, and Nullable
    /// and tuple definitions through the compilation's own type parts. Only a type key no symbol can be built for leaves the index's
    /// structural fallback.</summary>
    /// <param name="compilations">The scope's compilations.</param>
    /// <param name="symbols">The definitions and constructed types the scope names.</param>
    private static Func<string, string, bool?> SelfInterfaceConversions(IReadOnlyList<Compilation> compilations,
                                                                        IReadOnlyDictionary<string, ITypeSymbol> symbols)
    {
        IEnumerable<ITypeSymbol> Parts(ITypeSymbol type)
        {
            WorkCancellation.Check();
            yield return type;
            if (type is IArrayTypeSymbol array)
                foreach (var part in Parts(array.ElementType))
                {
                    WorkCancellation.Check();
                    yield return part;
                }
            if (type is INamedTypeSymbol named)
            {
                if (named.ContainingType is { } containing)
                    foreach (var part in Parts(containing))
                    {
                        WorkCancellation.Check();
                        yield return part;
                    }
                foreach (var argument in named.TypeArguments.Where(argument => argument.TypeKind != TypeKind.TypeParameter))
                {
                    WorkCancellation.Check();
                    foreach (var part in Parts(argument))
                    {
                        WorkCancellation.Check();
                        yield return part;
                    }
                }
            }
        }
        var available = symbols.Values.SelectMany(Parts).Distinct<ITypeSymbol>(SymbolEqualityComparer.Default).ToArray();
        var definitions = available.OfType<INamedTypeSymbol>().Select(type => type.OriginalDefinition)
                                 .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                                 .GroupBy(type => ProgramIndex.Shape(SymbolNames.TypeKey(type)).Shape, StringComparer.Ordinal)
                                 .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var resolved = new ConcurrentDictionary<string, ITypeSymbol>(available.GroupBy(SymbolNames.TypeKey, StringComparer.Ordinal)
                                                                            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal),
                                                                     StringComparer.Ordinal);
        var answers = new ConcurrentDictionary<(string Type, string Interface), bool>();
        INamedTypeSymbol Construct(INamedTypeSymbol definition, ITypeSymbol[] arguments, ref int offset)
        {
            WorkCancellation.Check();
            var type = definition.ContainingType is { } containing
                ? Construct(containing, arguments, ref offset).GetTypeMembers(definition.Name, definition.Arity).Single()
                : definition;
            var own = arguments.Skip(offset).Take(definition.Arity).ToArray();
            offset += definition.Arity;
            return own.Length == 0 ? type : type.Construct(own);
        }
        ITypeSymbol? Resolve(string key)
        {
            WorkCancellation.Check();
            if (resolved.TryGetValue(key, out var known))
                return known;
            if (key.EndsWith(']') && key.LastIndexOf('[') is var rankStart && rankStart > 0 &&
                key.AsSpan(rankStart + 1, key.Length - rankStart - 2).IndexOfAnyExcept(',') < 0)
            {
                if (Resolve(key[..rankStart]) is not { } element || compilations.FirstOrDefault() is not { } compilation)
                    return null;
                var array = compilation.CreateArrayTypeSymbol(element, key.Length - rankStart - 1);
                resolved.TryAdd(key, array);
                return array;
            }
            var (shape, arguments) = ProgramIndex.Shape(key);
            if (!definitions.TryGetValue(shape, out var definition) || arguments.Count == 0)
                return null;
            var bound = arguments.Select(Resolve).ToArray();
            if (bound.Any(argument => argument is null) || bound.Length != TypeArguments(definition).Count())
                return null;
            var offset = 0;
            var constructed = Construct(definition, bound!, ref offset);
            resolved.TryAdd(key, constructed);
            return constructed;
        }
        return (key, name) =>
        {
            if (answers.TryGetValue((key, name), out var answer))
                return answer;
            if (Resolve(key) is not { } type)
                return null;
            // The symbol's declaring compilation also sees its bases and interfaces, including source in another project.
            var compilation = compilations.FirstOrDefault(candidate => candidate.Assembly.Name == type.ContainingAssembly?.Name)
                              ?? compilations.FirstOrDefault();
            if (compilation is not CSharpCompilation csharp || csharp.GetTypeByMetadataName(name) is not { } @interface)
                return null;
            var conversion = csharp.ClassifyConversion(type, @interface.Construct(type));
            answer = conversion.IsImplicit && (conversion.IsIdentity || conversion.IsReference || conversion.IsBoxing);
            answers.TryAdd((key, name), answer);
            return answer;
        };
    }

    private static ProgramType Type(string typeKey, INamedTypeSymbol type, Compilation? source, CancellationToken cancellationToken)
    {
        WorkCancellation.Check();
        return new(typeKey, SymbolNames.Type(type), type.ContainingAssembly.Name,
                type.BaseType is null ? null : SymbolNames.TypeKey(type.BaseType),
                type.Interfaces.Select(SymbolNames.TypeKey).ToArray(),
                type.TypeKind == TypeKind.Interface, type.IsAbstract, type.IsSealed, type.TypeKind == TypeKind.Delegate, type.IsValueType,
                TypeArguments(type).Select(SymbolNames.TypeKey).ToArray())
            {
                IsSource = source is not null,
                InstanceFields = source is null ? [] : InstanceFields(type, source, cancellationToken),
                IsVisibleOutside = Derivation.IsVisible(type)
            };
    }

    /// <summary>The instance fields a source type declares, named as accesses name them: its fields, the backing fields of its
    /// automatic properties and of a record's positional ones, the backing field a property's accessors name with <c>field</c>,
    /// the storage of its field-like events, and the primary constructor parameters it captures (R3).</summary>
    /// <param name="type">The source type.</param>
    /// <param name="compilation">The compilation declaring it.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    private static IReadOnlyList<IrFieldRef> InstanceFields(INamedTypeSymbol type, Compilation compilation, CancellationToken cancellationToken)
    {
        // An accessor body reaches the backing field through `field` as an ordinary field; an automatic or positional property's
        // accesses name it through the property instead, below.
        var fields = type.GetMembers().OfType<IFieldSymbol>()
                         .Where(field => !field.IsStatic && !field.IsConst &&
                                         (!field.IsImplicitlyDeclared ||
                                          field.AssociatedSymbol is IPropertySymbol property && !IrLowering.IsAutoProperty(property, cancellationToken) &&
                                          !IsPositional(property, cancellationToken)))
                         .Select(IrLowering.FieldRef);
        var properties = type.GetMembers().OfType<IPropertySymbol>()
                             .Where(property => !property.IsStatic && !property.IsIndexer &&
                                                (IrLowering.IsAutoProperty(property, cancellationToken) || IsPositional(property, cancellationToken)))
                             .Select(IrLowering.PropertyField);
        var events = type.GetMembers().OfType<IEventSymbol>()
                         .Where(@event => !@event.IsStatic && FieldLikeEvents.Is(@event))
                         .Select(FieldLikeEvents.FieldRef);
        var parameters = type.InstanceConstructors
                             .Where(constructor => constructor.DeclaringSyntaxReferences.Any(reference =>
                                 reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax))
                             .SelectMany(constructor => IrLowering.CapturedPrimaryConstructorParameters(type, constructor, compilation, cancellationToken))
                             .Select(IrLowering.PrimaryConstructorParameterField);
        return fields.Concat(properties).Concat(events).Concat(parameters).ToArray();
    }

    /// <summary>A property a record declares by a parameter of its primary constructor: the compiler gives it a backing field.</summary>
    private static bool IsPositional(IPropertySymbol property, CancellationToken cancellationToken) =>
        property.ContainingType.IsRecord &&
        property.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax(cancellationToken) is ParameterSyntax);

    private static ProgramMethod Method(IMethodSymbol method, string typeKey, IReadOnlyDictionary<string, List<string>> implementations,
                                        IReadOnlyList<Compilation> compilations, CancellationToken cancellationToken)
    {
        WorkCancellation.Check();
        var methodId = IrLowering.RootBodyId(method);
        var hasSourceBody = HasSourceBody(method, compilations, cancellationToken);
        return new ProgramMethod(
            methodId,
            method.Name,
            SymbolNames.Method(method),
            typeKey,
            Kind(method),
            method.IsStatic,
            method.IsAbstract,
            method.IsVirtual,
            method.IsOverride,
            method.OverriddenMethod is { } overridden ? IrLowering.RootBodyId(overridden.OriginalDefinition) : null,
            implementations.GetValueOrDefault(methodId)?.ToArray() ?? [],
            method.Parameters.Select(parameter => new ProgramParameter(parameter.Name, SymbolNames.TypeKey(parameter.Type), RefKind(parameter.RefKind)))
                  .ToArray(),
            SymbolNames.TypeKey(method.ReturnType),
            hasSourceBody,
            hasSourceBody ? NestedBodyIds(method, compilations, cancellationToken) : [],
            method.TypeParameters.Select(SymbolNames.TypeKey).ToArray());
    }

    /// <summary>A declared body, the top-level statements' entry point, a primary constructor, an auto-property's synthesized accessor,
    /// the implicit constructor of a source class or struct, the implicit type initializer of a type with static initializers, and
    /// the compiler's add and remove accessors of a field-like event.</summary>
    /// <param name="method">The method.</param>
    /// <param name="compilations">The compilations of the scope.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    private static bool HasSourceBody(IMethodSymbol method, IReadOnlyList<Compilation> compilations, CancellationToken cancellationToken)
    {
        if (method.MethodKind is MethodKind.EventAdd or MethodKind.EventRemove &&
            method.AssociatedSymbol is IEventSymbol @event && FieldLikeEvents.Is(@event))
        {
            return true;
        }

        var type = method.ContainingType;
        if (method.DeclaringSyntaxReferences.Length == 0)
        {
            if (!method.IsImplicitlyDeclared || type.DeclaringSyntaxReferences.Length == 0)
                return false;
            return method.MethodKind switch
            {
                MethodKind.Constructor => method.Parameters.Length == 0 && type.TypeKind is TypeKind.Class or TypeKind.Struct,
                MethodKind.StaticConstructor => CompilationOf(type.DeclaringSyntaxReferences[0].SyntaxTree, compilations) is { } compilation &&
                                                IrLowering.Initializers(type, true, compilation, cancellationToken).Count != 0,
                _ => false
            };
        }

        return method.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) switch
        {
            BaseMethodDeclarationSyntax declaration => declaration.Body is not null || declaration.ExpressionBody is not null,
            AccessorDeclarationSyntax accessor => accessor.Body is not null || accessor.ExpressionBody is not null ||
                                                  method.AssociatedSymbol is IPropertySymbol property &&
                                                  IrLowering.IsAutoProperty(property, cancellationToken),
            ArrowExpressionClauseSyntax => true,
            CompilationUnitSyntax => method.MethodKind == MethodKind.Ordinary,
            TypeDeclarationSyntax => method.MethodKind == MethodKind.Constructor && !method.IsImplicitlyDeclared,
            _ => false
        };
    }

    private static IReadOnlyList<string> NestedBodyIds(IMethodSymbol method, IReadOnlyList<Compilation> compilations,
                                                       CancellationToken cancellationToken)
    {
        WorkCancellation.Check();
        var tree = method.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree ?? method.ContainingType.DeclaringSyntaxReferences[0].SyntaxTree;
        if (CompilationOf(tree, compilations) is not { } compilation)
            return [];
        return NestedBodies.GetValue(compilation, _ => new ConcurrentDictionary<IMethodSymbol, IReadOnlyList<string>>(SymbolEqualityComparer.Default))
                           .GetOrAdd(method, key => NestedBodyIds(key, compilation, cancellationToken));
    }

    /// <summary>The ids of the bodies nested in a source method — its lambdas and local functions at any depth — in source order.</summary>
    /// <param name="method">The method.</param>
    /// <param name="compilation">The compilation that declares it.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    private static IReadOnlyList<string> NestedBodyIds(IMethodSymbol method, Compilation compilation, CancellationToken cancellationToken)
    {
        WorkCancellation.Check();
        try
        {
            return IrLowering.NestedBodyIds(method, compilation, cancellationToken)
                             .OrderBy(pair => pair.Key.Tree.FilePath, StringComparer.Ordinal)
                             .ThenBy(pair => pair.Key.Span.Start)
                             .Select(pair => pair.Value)
                             .ToArray();
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    private static Compilation? CompilationOf(SyntaxTree tree, IReadOnlyList<Compilation> compilations) =>
        compilations.FirstOrDefault(compilation => compilation.ContainsSyntaxTree(tree));

    /// <summary>The types a compilation's syntax names, in the order a walk of its trees meets them: a generic name's type, then each
    /// of its type arguments, and the type of a <c>new</c> or a <c>typeof</c>. Built from the compilation alone, so every index over
    /// it reads the same list.</summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    private static MentionedType[] Mentioned(Compilation compilation, CancellationToken cancellationToken)
    {
        var mentioned = new List<MentionedType>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            WorkCancellation.Check();
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes())
            {
                WorkCancellation.Check();
                if (node is GenericNameSyntax name)
                {
                    var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
                    if (symbol is INamedTypeSymbol type)
                        mentioned.Add(new MentionedType(type, Immutable: false, Closed: true));
                    foreach (var argument in symbol switch
                             {
                                 INamedTypeSymbol named => named.TypeArguments,
                                 IMethodSymbol method => method.TypeArguments,
                                 _ => []
                             })
                    {
                        WorkCancellation.Check();
                        mentioned.Add(new MentionedType(argument, Immutable: true, Closed: true));
                    }
                }
                if (node is BaseObjectCreationExpressionSyntax creation && model.GetTypeInfo(creation, cancellationToken).Type is { } created)
                    mentioned.Add(new MentionedType(created, Immutable: true, Closed: false));
                if (node is TypeOfExpressionSyntax typeOf && model.GetTypeInfo(typeOf.Type, cancellationToken).Type is { } typed)
                    mentioned.Add(new MentionedType(typed, Immutable: true, Closed: false));
            }
        }

        return mentioned.ToArray();
    }

    /// <summary>The closed generic types a source type's declaration mentions: its base types, interfaces, field types and
    /// member signatures.</summary>
    /// <param name="type">The source type whose declaration is inspected.</param>
    /// <param name="closedTypes">Receives the closed generic types mentioned.</param>
    /// <param name="variantTypes">Receives the mentioned types and their supertypes.</param>
    private static void AddMentionedTypes(INamedTypeSymbol type, Dictionary<string, ClosedGenericType> closedTypes,
                                          Dictionary<string, ProgramVariantType> variantTypes)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            WorkCancellation.Check();
            AddClosed(baseType, closedTypes, variantTypes);
        }
        foreach (var @interface in type.AllInterfaces)
        {
            WorkCancellation.Check();
            AddClosed(@interface, closedTypes, variantTypes);
        }
        foreach (var member in type.GetMembers())
        {
            WorkCancellation.Check();
            switch (member)
            {
                case IFieldSymbol field:
                    AddClosed(field.Type, closedTypes, variantTypes);
                    break;
                case IPropertySymbol property:
                    AddClosed(property.Type, closedTypes, variantTypes);
                    break;
                case IMethodSymbol method:
                    AddClosed(method.ReturnType, closedTypes, variantTypes);
                    foreach (var parameter in method.Parameters)
                    {
                        WorkCancellation.Check();
                        AddClosed(parameter.Type, closedTypes, variantTypes);
                    }
                    break;
            }
        }
    }

    private static void AddClosed(ITypeSymbol type, Dictionary<string, ClosedGenericType> closedTypes,
                                  Dictionary<string, ProgramVariantType> variantTypes)
    {
        WorkCancellation.Check();
        if (type is INamedTypeSymbol namedType && !ContainsTypeParameter(namedType))
        {
            var supertypes = new List<string> { SymbolNames.TypeKey(type) };
            for (var current = namedType.BaseType; current is not null; current = current.BaseType)
            {
                WorkCancellation.Check();
                supertypes.Add(SymbolNames.TypeKey(current));
            }
            supertypes.AddRange(namedType.AllInterfaces.Select(SymbolNames.TypeKey));
            variantTypes.TryAdd(SymbolNames.TypeKey(type), new ProgramVariantType(SymbolNames.TypeKey(type), type.IsReferenceType, supertypes));
        }
        switch (type)
        {
            case IArrayTypeSymbol array:
                AddClosed(array.ElementType, closedTypes, variantTypes);
                break;
            case INamedTypeSymbol named:
                foreach (var argument in TypeArguments(named))
                {
                    WorkCancellation.Check();
                    AddClosed(argument, closedTypes, variantTypes);
                }
                if (!SymbolEqualityComparer.Default.Equals(named, named.OriginalDefinition) && !named.IsUnboundGenericType &&
                    !TypeArguments(named).Any(ContainsTypeParameter))
                {
                    closedTypes.TryAdd(SymbolNames.TypeKey(named), new ClosedGenericType(
                        SymbolNames.TypeKey(named), SymbolNames.TypeKey(named.OriginalDefinition),
                        TypeArguments(named).Select(SymbolNames.TypeKey).ToArray()));
                }
                break;
        }
    }

    /// <summary>The type arguments of a type and of the types containing it, outermost first.</summary>
    /// <param name="type">The type whose arguments and containing types are inspected.</param>
    private static IEnumerable<ITypeSymbol> TypeArguments(INamedTypeSymbol type)
    {
        WorkCancellation.Check();
        return (type.ContainingType is { } containing ? TypeArguments(containing) : []).Concat(type.TypeArguments);
    }

    private static bool ContainsTypeParameter(ITypeSymbol type)
    {
        WorkCancellation.Check();
        return type switch
        {
            ITypeParameterSymbol => true,
            IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
            INamedTypeSymbol named => TypeArguments(named).Any(ContainsTypeParameter),
            _ => false
        };
    }

    private static ProgramMethodKind Kind(IMethodSymbol method) => method.MethodKind switch
    {
        MethodKind.Constructor => ProgramMethodKind.Constructor,
        MethodKind.StaticConstructor => ProgramMethodKind.TypeInitializer,
        MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise =>
            ProgramMethodKind.Accessor,
        MethodKind.UserDefinedOperator or MethodKind.Conversion or MethodKind.BuiltinOperator => ProgramMethodKind.Operator,
        _ => ProgramMethodKind.Ordinary
    };

    private static IrRefKind RefKind(Microsoft.CodeAnalysis.RefKind kind) => kind switch
    {
        Microsoft.CodeAnalysis.RefKind.Ref => IrRefKind.Ref,
        Microsoft.CodeAnalysis.RefKind.Out => IrRefKind.Out,
        Microsoft.CodeAnalysis.RefKind.In => IrRefKind.In,
        Microsoft.CodeAnalysis.RefKind.RefReadOnlyParameter => IrRefKind.RefReadOnly,
        _ => IrRefKind.None
    };

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol @namespace)
    {
        foreach (var type in @namespace.GetTypeMembers().SelectMany(NestedAndSelf))
        {
            WorkCancellation.Check();
            if (type.DeclaringSyntaxReferences.Length != 0)
                yield return type;
        }

        foreach (var nested in @namespace.GetNamespaceMembers().SelectMany(SourceTypes))
        {
            WorkCancellation.Check();
            yield return nested;
        }
    }

    private static IEnumerable<INamedTypeSymbol> NestedAndSelf(INamedTypeSymbol type) =>
        new[] { type }.Concat(type.GetTypeMembers().SelectMany(NestedAndSelf));
}
