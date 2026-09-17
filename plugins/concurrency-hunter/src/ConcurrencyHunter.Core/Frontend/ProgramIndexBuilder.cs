using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConcurrencyHunter.Frontend;

/// <summary>Builds the program index of a process scope: every source type of its compilations and every metadata type a source
/// type derives from or implements, their methods and fields, and the closed generic types source mentions.</summary>
public static class ProgramIndexBuilder
{
    public static ProgramIndex Build(string scopeId, IReadOnlyList<Compilation> compilations, string rootDirectory,
                                     CancellationToken cancellationToken)
    {
        // Source types first, each from its own compilation, so a base type declared in another project of the scope is that
        // project's symbol.
        var sourceTypes = compilations.SelectMany(compilation => SourceTypes(compilation.Assembly.GlobalNamespace)).ToArray();
        var types = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        foreach (var type in sourceTypes)
            types.TryAdd(SymbolNames.TypeKey(type), type);

        var closedTypes = new Dictionary<string, ClosedGenericType>(StringComparer.Ordinal);
        foreach (var type in sourceTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
                types.TryAdd(SymbolNames.TypeKey(baseType.OriginalDefinition), baseType.OriginalDefinition);
            foreach (var @interface in type.AllInterfaces)
                types.TryAdd(SymbolNames.TypeKey(@interface.OriginalDefinition), @interface.OriginalDefinition);
            AddMentionedTypes(type, closedTypes);
        }

        foreach (var compilation in compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var name in tree.GetRoot(cancellationToken).DescendantNodes().OfType<GenericNameSyntax>())
                {
                    if (model.GetSymbolInfo(name, cancellationToken).Symbol is INamedTypeSymbol mentioned)
                        AddClosed(mentioned, closedTypes);
                }
            }
        }

        var implementations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var type in types.Values.Where(type => type.TypeKind != TypeKind.Interface))
        {
            foreach (var @interface in type.AllInterfaces)
            {
                foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>())
                {
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
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var methodId = IrLowering.RootBodyId(method);
                if (!methods.ContainsKey(methodId))
                    methods.Add(methodId, Method(method, typeKey, implementations, compilations, cancellationToken));
            }

            fields.AddRange(type.GetMembers().OfType<IFieldSymbol>()
                                .Where(field => !field.IsImplicitlyDeclared)
                                .Select(field => new ProgramField(typeKey, field.Name, field.IsStatic, field.IsReadOnly,
                                                                  SymbolNames.TypeKey(field.Type))));
        }

        return new ProgramIndex(scopeId, types.Select(pair => Type(pair.Key, pair.Value)).ToArray(), methods.Values.ToArray(), fields,
                                closedTypes.Values.ToArray());
    }

    private static ProgramType Type(string typeKey, INamedTypeSymbol type) =>
        new(typeKey, SymbolNames.Type(type), type.ContainingAssembly.Name,
            type.BaseType is null ? null : SymbolNames.TypeKey(type.BaseType),
            type.Interfaces.Select(SymbolNames.TypeKey).ToArray(),
            type.TypeKind == TypeKind.Interface, type.IsAbstract, type.IsSealed, type.TypeKind == TypeKind.Delegate, type.IsValueType,
            TypeArguments(type).Select(SymbolNames.TypeKey).ToArray());

    private static ProgramMethod Method(IMethodSymbol method, string typeKey, IReadOnlyDictionary<string, List<string>> implementations,
                                        IReadOnlyList<Compilation> compilations, CancellationToken cancellationToken)
    {
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
    /// the implicit constructor of a source class or struct, and the implicit type initializer of a type with static initializers.</summary>
    private static bool HasSourceBody(IMethodSymbol method, IReadOnlyList<Compilation> compilations, CancellationToken cancellationToken)
    {
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
        var tree = method.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree ?? method.ContainingType.DeclaringSyntaxReferences[0].SyntaxTree;
        if (CompilationOf(tree, compilations) is not { } compilation)
            return [];
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

    /// <summary>The closed generic types a source type's declaration mentions: its base types, interfaces, field types and
    /// member signatures.</summary>
    private static void AddMentionedTypes(INamedTypeSymbol type, Dictionary<string, ClosedGenericType> closedTypes)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            AddClosed(baseType, closedTypes);
        foreach (var @interface in type.AllInterfaces)
            AddClosed(@interface, closedTypes);
        foreach (var member in type.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol field:
                    AddClosed(field.Type, closedTypes);
                    break;
                case IPropertySymbol property:
                    AddClosed(property.Type, closedTypes);
                    break;
                case IMethodSymbol method:
                    AddClosed(method.ReturnType, closedTypes);
                    foreach (var parameter in method.Parameters)
                        AddClosed(parameter.Type, closedTypes);
                    break;
            }
        }
    }

    private static void AddClosed(ITypeSymbol type, Dictionary<string, ClosedGenericType> closedTypes)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                AddClosed(array.ElementType, closedTypes);
                break;
            case INamedTypeSymbol named:
                foreach (var argument in TypeArguments(named))
                    AddClosed(argument, closedTypes);
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
    private static IEnumerable<ITypeSymbol> TypeArguments(INamedTypeSymbol type) =>
        (type.ContainingType is { } containing ? TypeArguments(containing) : []).Concat(type.TypeArguments);

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        INamedTypeSymbol named => TypeArguments(named).Any(ContainsTypeParameter),
        _ => false
    };

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
            if (type.DeclaringSyntaxReferences.Length != 0)
                yield return type;
        }

        foreach (var nested in @namespace.GetNamespaceMembers().SelectMany(SourceTypes))
            yield return nested;
    }

    private static IEnumerable<INamedTypeSymbol> NestedAndSelf(INamedTypeSymbol type) =>
        new[] { type }.Concat(type.GetTypeMembers().SelectMany(NestedAndSelf));
}
