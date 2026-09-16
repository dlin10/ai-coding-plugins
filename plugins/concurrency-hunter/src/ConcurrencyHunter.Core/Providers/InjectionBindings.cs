using ConcurrencyHunter.Di;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace ConcurrencyHunter.Providers;

/// <summary>
/// Binds the members of a source type that hold what the container passed to its one public constructor: a
/// readonly field or get-only auto-property every assignment of which, in that constructor and in initializers,
/// is one never-written constructor parameter, and a captured primary-constructor parameter that is never
/// written. Members declared by a base type bind only through pass-through <c>base(...)</c> arguments.
/// </summary>
public static class InjectionBindings
{
    public static IReadOnlyList<TypeInjectionBindings> Discover(IReadOnlyList<Compilation> compilations, DiIndex index,
                                                               string rootDirectory, CancellationToken cancellationToken)
    {
        var results = new List<TypeInjectionBindings>();
        foreach (var compilation in compilations)
        {
            foreach (var type in SourceTypes(compilation.Assembly.GlobalNamespace))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (type.TypeKind == TypeKind.Class && !type.IsStatic && !type.IsAbstract && !type.IsImplicitlyDeclared)
                    results.Add(Bind(type, compilations, index, rootDirectory, cancellationToken));
            }
        }

        return results;
    }

    public static TypeInjectionBindings Bind(INamedTypeSymbol type, IReadOnlyList<Compilation> compilations, DiIndex index,
                                             string rootDirectory, CancellationToken cancellationToken) =>
        new Binder(type, compilations, index, rootDirectory, cancellationToken).Bind();

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

    private enum Use
    {
        Read,
        SimpleAssignment,
        OtherWrite,
        RefOrOut,
        In
    }

    private sealed record Reference(IdentifierNameSyntax Node, Use Use, AssignmentExpressionSyntax? Assignment);

    /// <summary>A store into a member: an assignment in a constructor or an initializer, with the constructor
    /// parameter it stores when it stores exactly one and nothing else.</summary>
    private sealed record Store(SyntaxNode Syntax, IMethodSymbol? Constructor, IParameterSymbol? Parameter);

    private sealed record Candidate(ISymbol Symbol, InjectionMemberKind Kind);

    private sealed class Binder(INamedTypeSymbol type, IReadOnlyList<Compilation> compilations, DiIndex index,
                                string rootDirectory, CancellationToken cancellationToken)
    {
        private readonly string _typeName = SymbolNames.Type(type);
        private readonly List<InjectionBinding> _bindings = [];
        private readonly List<DiDiagnostic> _diagnostics = [];
        private IReadOnlyList<ConstructorParameterResolution> _constructorParameters = [];

        internal TypeInjectionBindings Bind()
        {
            var publicConstructors = type.InstanceConstructors.Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public).ToArray();
            if (publicConstructors.Length != 1)
            {
                if (HasInjectionShape())
                {
                    Diagnose(type, $"{_typeName} has {publicConstructors.Length} public constructors; injection bindings need exactly one.");
                }
                return Result();
            }

            var constructor = publicConstructors[0];
            _constructorParameters = constructor.Parameters
                                                .Select(parameter => new ConstructorParameterResolution(
                                                    parameter.Name, parameter.Ordinal, SymbolNames.TypeKey(parameter.Type),
                                                    index.Resolve(SymbolNames.TypeKey(parameter.Type))))
                                                .ToArray();
            var origins = new Dictionary<IParameterSymbol, IParameterSymbol>(SymbolEqualityComparer.Default);
            foreach (var parameter in constructor.Parameters.Where(parameter => !IsWrittenInConstructor(type, constructor, parameter)))
                origins[parameter] = parameter;
            var currentType = type;
            var constructedType = type;
            var currentConstructor = constructor;
            string? chainBreak = null;
            var linkEvidence = new List<BindingEvidence>();
            while (true)
            {
                BindLevel(currentType, constructedType, currentConstructor, origins, chainBreak, linkEvidence);

                var baseType = currentType.BaseType;
                if (baseType is null || baseType.DeclaringSyntaxReferences.Length == 0)
                    break;

                var link = BaseLink(currentType, currentConstructor, baseType.OriginalDefinition);
                chainBreak ??= link.Break;
                var nextOrigins = new Dictionary<IParameterSymbol, IParameterSymbol>(SymbolEqualityComparer.Default);
                foreach (var (argument, parameter) in link.Arguments)
                {
                    if (argument is not null && origins.TryGetValue(argument, out var origin))
                    {
                        nextOrigins[parameter] = origin;
                        linkEvidence.Add(new BindingEvidence("base-argument",
                            $"{SymbolNames.Type(currentType)} passes {argument.Name} to {SymbolNames.Type(baseType)} parameter {parameter.Name}",
                            SourceSpans.From(link.Syntax!, rootDirectory)));
                    }
                }

                currentType = baseType.OriginalDefinition;
                constructedType = constructedType.BaseType!;
                currentConstructor = link.Constructor ?? currentType.InstanceConstructors.FirstOrDefault(candidate => candidate.Parameters.Length == 0)
                                     ?? currentType.InstanceConstructors.First();
                origins = nextOrigins;
            }

            return Result();
        }

        private TypeInjectionBindings Result() => new(_typeName, _bindings, _diagnostics, SymbolNames.TypeKey(type), _constructorParameters);

        /// <summary>Binds the members <paramref name="declaringType"/> declares. <paramref name="constructedType"/> is that type as
        /// the bound type inherits it (a closed generic base), which is how lowering names the members' containing type.</summary>
        private void BindLevel(INamedTypeSymbol declaringType, INamedTypeSymbol constructedType, IMethodSymbol constructor,
                               IReadOnlyDictionary<IParameterSymbol, IParameterSymbol> origins, string? chainBreak,
                               IReadOnlyList<BindingEvidence> linkEvidence)
        {
            var inherited = !SymbolEqualityComparer.Default.Equals(declaringType, type.OriginalDefinition);
            foreach (var candidate in Candidates(declaringType))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var member = $"{SymbolNames.Type(constructedType)}.{candidate.Symbol.Name}";
                var (parameter, stores) = candidate.Kind == InjectionMemberKind.PrimaryConstructorParameter
                    ? CapturedParameter(declaringType, constructor, (IParameterSymbol)candidate.Symbol)
                    : StoredParameter(declaringType, constructor, candidate.Symbol);
                if (parameter is null)
                {
                    if (inherited && stores is not null)
                        Diagnose(candidate.Symbol, $"{member} is not bound for {_typeName}: {chainBreak ?? "it is assigned outside the invoked base constructor"}.");
                    continue;
                }

                if (!origins.TryGetValue(parameter, out var origin))
                {
                    if (inherited)
                    {
                        Diagnose(candidate.Symbol, $"{member} is not bound for {_typeName}: " +
                                                   (chainBreak ?? $"the base constructor argument for {parameter.Name} is not a pass-through constructor parameter") + ".");
                    }
                    continue;
                }

                if (origin.Type.IsValueType)
                {
                    Diagnose(candidate.Symbol, $"{member} is not bound for {_typeName}: value-type service.");
                    continue;
                }

                var serviceType = SymbolNames.Type(origin.Type);
                var resolution = index.Resolve(SymbolNames.TypeKey(origin.Type));
                var evidence = stores!.Select(store => new BindingEvidence(
                                              "assignment", $"{member} holds constructor parameter {parameter.Name}",
                                              SourceSpans.From(store.Syntax, rootDirectory)))
                                      .Concat(inherited ? linkEvidence : [])
                                      .Concat(resolution.Registrations.Select(registration => new BindingEvidence(
                                          "registration", $"{registration.Method} registers {serviceType}", registration.Source)))
                                      .ToArray();
                _bindings.Add(new InjectionBinding(_typeName, SymbolNames.Type(constructedType), candidate.Symbol.Name, candidate.Kind,
                                                   origin.Name, origin.Ordinal, serviceType, resolution, evidence,
                                                   SymbolNames.TypeIdentity(constructedType)));
            }
        }

        /// <summary>The constructor parameter a readonly field or get-only auto-property holds, or null. The stores
        /// are returned whenever every store is some constructor parameter, so an inherited member whose chain
        /// breaks can still be reported.</summary>
        private (IParameterSymbol? Parameter, IReadOnlyList<Store>? Stores) StoredParameter(INamedTypeSymbol declaringType,
                                                                                            IMethodSymbol constructor, ISymbol member)
        {
            var references = References(declaringType, member).ToArray();
            if (references.Any(reference => reference.Use is Use.RefOrOut or Use.In or Use.OtherWrite) ||
                DerivedTypes(declaringType).SelectMany(derived => References(derived, member)).Any(reference => reference.Use != Use.Read))
            {
                return (null, null);
            }

            var stores = references.Where(reference => reference.Use == Use.SimpleAssignment)
                                   .Select(reference => AssignmentStore(reference.Assignment!))
                                   .Concat(InitializerStores(declaringType, member))
                                   .ToArray();
            if (stores.Length == 0 || stores.Any(store => store.Parameter is null))
                return (null, null);

            var parameters = stores.Select(store => store.Parameter).Distinct(SymbolEqualityComparer.Default).ToArray();
            if (parameters.Length != 1 ||
                stores.Any(store => !SymbolEqualityComparer.Default.Equals(store.Constructor, constructor)) ||
                IsWrittenInConstructor(declaringType, constructor, stores[0].Parameter!))
            {
                return (null, stores);
            }

            return (stores[0].Parameter, stores);
        }

        /// <summary>The types of the chain from the bound type up to, not including, <paramref name="declaringType"/>: a member
        /// they inherit is written or passed by reference there as much as in its declaring type.</summary>
        private IEnumerable<INamedTypeSymbol> DerivedTypes(INamedTypeSymbol declaringType)
        {
            for (var current = type.OriginalDefinition;
                 current is not null && !SymbolEqualityComparer.Default.Equals(current, declaringType);
                 current = current.BaseType?.OriginalDefinition)
            {
                yield return current;
            }
        }

        private (IParameterSymbol? Parameter, IReadOnlyList<Store>? Stores) CapturedParameter(INamedTypeSymbol declaringType,
                                                                                              IMethodSymbol constructor,
                                                                                              IParameterSymbol parameter)
        {
            var references = References(declaringType, parameter).ToArray();
            var declaration = SyntaxOf(parameter);
            IReadOnlyList<Store> stores = declaration is null ? [] : [new Store(declaration, PrimaryConstructor(declaringType), parameter)];
            if (references.Any(reference => reference.Use != Use.Read))
                return (null, null);
            if (!SymbolEqualityComparer.Default.Equals(PrimaryConstructor(declaringType), constructor))
                return (null, stores);
            return (parameter, stores);
        }

        private Store AssignmentStore(AssignmentExpressionSyntax assignment)
        {
            var model = Model(assignment.SyntaxTree);
            var constructorSyntax = assignment.FirstAncestorOrSelf<ConstructorDeclarationSyntax>();
            var constructor = constructorSyntax is null ? null : model.GetDeclaredSymbol(constructorSyntax, cancellationToken);
            var value = Parameter(model, assignment.Right);
            var isThis = assignment.Left is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };
            var isSameConstructor = value is not null && SymbolEqualityComparer.Default.Equals(value.ContainingSymbol, constructor);
            return new Store(assignment, constructor, isThis && isSameConstructor && IsDirectlyInConstructor(assignment, constructorSyntax) ? value : null);
        }

        private IEnumerable<Store> InitializerStores(INamedTypeSymbol declaringType, ISymbol member)
        {
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax(cancellationToken);
                var initializer = syntax switch
                {
                    VariableDeclaratorSyntax declarator => declarator.Initializer,
                    PropertyDeclarationSyntax property => property.Initializer,
                    _ => null
                };
                if (initializer is null)
                    continue;

                var primary = PrimaryConstructor(declaringType);
                var value = Parameter(Model(initializer.SyntaxTree), initializer.Value);
                var isPrimaryParameter = value is not null && primary is not null &&
                                         SymbolEqualityComparer.Default.Equals(value.ContainingSymbol, primary);
                yield return new Store(initializer, primary, isPrimaryParameter ? value : null);
            }
        }

        private static bool IsDirectlyInConstructor(SyntaxNode node, ConstructorDeclarationSyntax? constructor) =>
            constructor is not null &&
            !node.Ancestors().TakeWhile(ancestor => ancestor != constructor)
                 .Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

        private IParameterSymbol? Parameter(SemanticModel model, ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression })
            {
                expression = expression is ParenthesizedExpressionSyntax parenthesized
                    ? parenthesized.Expression
                    : ((PostfixUnaryExpressionSyntax)expression).Operand;
            }
            return expression is IdentifierNameSyntax identifier
                ? model.GetSymbolInfo(identifier, cancellationToken).Symbol as IParameterSymbol
                : null;
        }

        private bool IsWrittenInConstructor(INamedTypeSymbol declaringType, IMethodSymbol constructor, IParameterSymbol parameter)
        {
            var regions = ConstructorRegions(declaringType, constructor).ToArray();
            return References(declaringType, parameter)
                .Any(reference => reference.Use is Use.SimpleAssignment or Use.OtherWrite or Use.RefOrOut &&
                                  regions.Any(region => region.SyntaxTree == reference.Node.SyntaxTree &&
                                                        region.Span.Contains(reference.Node.Span)));
        }

        private IEnumerable<SyntaxNode> ConstructorRegions(INamedTypeSymbol declaringType, IMethodSymbol constructor)
        {
            foreach (var reference in constructor.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax(cancellationToken);
                if (syntax is ConstructorDeclarationSyntax)
                {
                    yield return syntax;
                }
                else if (syntax is TypeDeclarationSyntax declaration)
                {
                    foreach (var primaryBase in declaration.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>() ?? [])
                        yield return primaryBase;
                }
            }

            foreach (var declaration in Declarations(declaringType))
            {
                foreach (var initializer in declaration.Members.SelectMany(Initializers))
                    yield return initializer;
            }
        }

        private static IEnumerable<EqualsValueClauseSyntax> Initializers(MemberDeclarationSyntax member) => member switch
        {
            FieldDeclarationSyntax field => field.Declaration.Variables.Select(variable => variable.Initializer).OfType<EqualsValueClauseSyntax>(),
            PropertyDeclarationSyntax { Initializer: not null } property => [property.Initializer],
            _ => []
        };

        private (IMethodSymbol? Constructor, SyntaxNode? Syntax, string? Break, IReadOnlyList<(IParameterSymbol? Argument, IParameterSymbol Parameter)> Arguments)
            BaseLink(INamedTypeSymbol derivedType, IMethodSymbol constructor, INamedTypeSymbol baseType)
        {
            const string IMPLICIT = "the base constructor is called implicitly without arguments";
            foreach (var reference in constructor.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax(cancellationToken);
                ArgumentListSyntax? arguments = null;
                SyntaxNode? linkSyntax = null;
                if (syntax is ConstructorDeclarationSyntax declaration)
                {
                    if (declaration.Initializer is null)
                        return (null, null, IMPLICIT, []);
                    if (declaration.Initializer.IsKind(SyntaxKind.ThisConstructorInitializer))
                        return (null, null, "the constructor delegates to another constructor with this(...)", []);
                    arguments = declaration.Initializer.ArgumentList;
                    linkSyntax = declaration.Initializer;
                }
                else if (syntax is TypeDeclarationSyntax typeDeclaration)
                {
                    var primaryBase = typeDeclaration.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault();
                    if (primaryBase is null)
                        continue;
                    arguments = primaryBase.ArgumentList;
                    linkSyntax = primaryBase;
                }

                if (arguments is null || linkSyntax is null)
                    continue;

                var model = Model(linkSyntax.SyntaxTree);
                if (model.GetSymbolInfo(linkSyntax, cancellationToken).Symbol is not IMethodSymbol baseConstructor)
                    return (null, null, "the base constructor call does not resolve", []);

                baseConstructor = baseConstructor.OriginalDefinition;
                var links = new List<(IParameterSymbol?, IParameterSymbol)>();
                for (var position = 0; position < arguments.Arguments.Count; position++)
                {
                    var argument = arguments.Arguments[position];
                    var parameter = argument.NameColon is { } name
                        ? baseConstructor.Parameters.FirstOrDefault(candidate => candidate.Name == name.Name.Identifier.ValueText)
                        : position < baseConstructor.Parameters.Length ? baseConstructor.Parameters[position] : null;
                    if (parameter is null)
                        continue;
                    var passed = argument.RefKindKeyword.IsKind(SyntaxKind.None) && parameter.RefKind == RefKind.None
                        ? Parameter(model, argument.Expression)
                        : null;
                    links.Add((passed, parameter));
                }

                return (baseConstructor, linkSyntax, null, links);
            }

            return (null, null, IMPLICIT, []);
        }

        private IEnumerable<Candidate> Candidates(INamedTypeSymbol declaringType)
        {
            foreach (var member in declaringType.GetMembers())
            {
                switch (member)
                {
                    case IFieldSymbol { IsStatic: false, IsConst: false, IsReadOnly: true, IsImplicitlyDeclared: false } field:
                        yield return new Candidate(field, InjectionMemberKind.Field);
                        break;
                    case IPropertySymbol { IsStatic: false, IsIndexer: false, SetMethod: null, GetMethod: not null } property
                        when IsAutoProperty(property):
                        yield return new Candidate(property, InjectionMemberKind.AutoProperty);
                        break;
                }
            }

            if (PrimaryConstructor(declaringType) is { } primary)
            {
                foreach (var parameter in primary.Parameters.Where(parameter => IsCaptured(declaringType, parameter)))
                    yield return new Candidate(parameter, InjectionMemberKind.PrimaryConstructorParameter);
            }
        }

        private bool IsAutoProperty(IPropertySymbol property) =>
            property.DeclaringSyntaxReferences.Length != 0 &&
            property.DeclaringSyntaxReferences.All(reference =>
                reference.GetSyntax(cancellationToken) is PropertyDeclarationSyntax { AccessorList: { } accessors } &&
                accessors.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null));

        private bool IsCaptured(INamedTypeSymbol declaringType, IParameterSymbol parameter)
        {
            var primary = PrimaryConstructor(declaringType)!;
            var regions = ConstructorRegions(declaringType, primary).ToArray();
            return References(declaringType, parameter).Any(reference =>
                !regions.Any(region => region.SyntaxTree == reference.Node.SyntaxTree && region.Span.Contains(reference.Node.Span)));
        }

        private bool HasInjectionShape()
        {
            for (var current = type; current is not null && current.DeclaringSyntaxReferences.Length != 0; current = current.BaseType)
            {
                if (Candidates(current).Any() || current.InstanceConstructors.Any(constructor => constructor.Parameters.Length != 0))
                    return true;
            }

            return false;
        }

        private static IMethodSymbol? PrimaryConstructor(INamedTypeSymbol declaringType) =>
            declaringType.InstanceConstructors.FirstOrDefault(constructor =>
                constructor.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is TypeDeclarationSyntax));

        private IEnumerable<TypeDeclarationSyntax> Declarations(INamedTypeSymbol declaringType) =>
            declaringType.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(cancellationToken)).OfType<TypeDeclarationSyntax>();

        private IEnumerable<Reference> References(INamedTypeSymbol declaringType, ISymbol symbol)
        {
            var target = symbol.OriginalDefinition;
            foreach (var declaration in Declarations(declaringType))
            {
                var model = Model(declaration.SyntaxTree);
                foreach (var identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (identifier.Identifier.ValueText != symbol.Name ||
                        !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol?.OriginalDefinition, target))
                    {
                        continue;
                    }

                    yield return Classify(model, identifier);
                }
            }
        }

        private Reference Classify(SemanticModel model, IdentifierNameSyntax identifier)
        {
            ExpressionSyntax expression = identifier;
            var ownReceiver = true;
            if (identifier.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == identifier)
            {
                expression = memberAccess;
                ownReceiver = memberAccess.Expression is ThisExpressionSyntax;
            }

            while (expression.Parent is ParenthesizedExpressionSyntax or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression })
                expression = (ExpressionSyntax)expression.Parent;

            switch (expression.Parent)
            {
                case AssignmentExpressionSyntax assignment when assignment.Left == expression:
                    return assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && ownReceiver
                        ? new Reference(identifier, Use.SimpleAssignment, assignment)
                        : new Reference(identifier, Use.OtherWrite, null);
                case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    return new Reference(identifier, Use.OtherWrite, null);
                case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword):
                    return new Reference(identifier, argument.Parent?.Parent is TupleExpressionSyntax ? Use.OtherWrite : Use.RefOrOut, null);
                case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword):
                    return new Reference(identifier, Use.In, null);
                // A caller may omit `in` (and `ref readonly` accepts no keyword): the parameter decides, not the syntax.
                case ArgumentSyntax argument when model.GetOperation(argument, cancellationToken) is IArgumentOperation
                                                  {
                                                      Parameter.RefKind: RefKind.Ref or RefKind.Out or RefKind.In or RefKind.RefReadOnlyParameter
                                                  }:
                    return new Reference(identifier, Use.In, null);
                case ArgumentSyntax { Parent: TupleExpressionSyntax tuple } when IsDeconstructionTarget(tuple):
                    return new Reference(identifier, Use.OtherWrite, null);
                case RefExpressionSyntax:
                    return new Reference(identifier, Use.RefOrOut, null);
                default:
                    return new Reference(identifier, Use.Read, null);
            }
        }

        private static bool IsDeconstructionTarget(TupleExpressionSyntax tuple)
        {
            SyntaxNode current = tuple;
            while (current.Parent is ArgumentSyntax { Parent: TupleExpressionSyntax outer })
                current = outer;
            return current.Parent is AssignmentExpressionSyntax assignment && assignment.Left == current;
        }

        private SyntaxNode? SyntaxOf(ISymbol symbol) =>
            symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);

        private SemanticModel Model(SyntaxTree tree) =>
            compilations.FirstOrDefault(compilation => compilation.ContainsSyntaxTree(tree))?.GetSemanticModel(tree)
            ?? throw new InvalidOperationException($"No compilation of the scope contains {tree.FilePath}.");

        private void Diagnose(ISymbol symbol, string message)
        {
            var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
            _diagnostics.Add(new DiDiagnostic(RootDiscoveryDiagnosticCode.UnresolvedBinding, _typeName, message,
                                              location is null ? null : SourceSpans.From(location, rootDirectory)));
        }
    }
}
