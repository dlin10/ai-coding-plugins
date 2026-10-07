using System.Diagnostics;
using System.Globalization;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace ConcurrencyHunter.Frontend;

public sealed record IrLoweredMethod(IrBody Body, IReadOnlyList<IrBody> NestedBodies)
{
    public IReadOnlyDictionary<string, IReadOnlySet<string>> MetadataSupertypes { get; init; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
}

public static class IrLowering
{
    private const string THIS_KEY = "this";

    private static readonly System.Text.RegularExpressions.Regex NESTED_BODY_SUFFIX =
        new(@"(?:#lambda\d+|#local:[^#~]+(?:~\d+)?)+$");

    /// <summary>Lowers a member with a source body. A constructor's body runs its type's initializers (unless it chains to
    /// <c>this(...)</c>), then the base or <c>this</c> call, then its own body; a type initializer runs the static initializers,
    /// then the static constructor body; an auto-property accessor loads or stores its backing field.</summary>
    /// <param name="method">The member to lower.</param>
    /// <param name="compilation">The compilation the member is declared in.</param>
    /// <param name="rootDirectory">The root directory source locations are made relative to.</param>
    /// <param name="cancellationToken">Cancels the lowering.</param>
    public static IrLoweredMethod Lower(IMethodSymbol method, Compilation compilation, string rootDirectory,
                                        CancellationToken cancellationToken) =>
        Lower(method, compilation, rootDirectory, cancellationToken, LibraryModels.BuiltIn);

    public static IrLoweredMethod Lower(IMethodSymbol method, Compilation compilation, string rootDirectory,
                                        CancellationToken cancellationToken, LibraryModels libraryModels)
    {
        var plan = Plan(method, compilation, cancellationToken);
        var nestedIds = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
        var functions = new List<(ControlFlowGraph Graph, IMethodSymbol[] LocalFunctions, IFlowAnonymousFunctionOperation[] AnonymousFunctions)>();
        foreach (var segment in plan.Segments.OfType<GraphSegment>())
        {
            var lambdas = functions.Sum(function => function.AnonymousFunctions.Length);
            var (localFunctions, anonymousFunctions, ids) = AssignNestedIds(segment.Graph, plan.BodyId, nestedIds, lambdas);
            nestedIds = ids;
            functions.Add((segment.Graph, localFunctions, anonymousFunctions));
        }

        var context = new LoweringContext(plan.BodyId, new SiteOrdinals(plan.Roots, compilation, cancellationToken), rootDirectory,
                                          compilation, cancellationToken, libraryModels);
        var ownerSymbol = SymbolNames.Method(method);
        var body = new BodyLowerer(method, plan.Segments, plan.BodyId, ownerSymbol, !method.IsStatic, nestedIds, context).Lower();
        var nestedBodies = new List<IrBody>();
        foreach (var (graph, localFunctions, anonymousFunctions) in functions)
            nestedBodies.AddRange(LowerNestedFunctions(graph, localFunctions, anonymousFunctions, ownerSymbol, !method.IsStatic, nestedIds, context));
        return new IrLoweredMethod(body, nestedBodies) { MetadataSupertypes = context.MetadataSupertypes };
    }

    /// <summary>The IR body id of every lambda and local function nested in <paramref name="method"/>, at any depth,
    /// keyed by the span of the nested function's declaring syntax; the ids are those <see cref="Lower"/> assigns.</summary>
    /// <param name="method">The method whose nested functions are collected.</param>
    /// <param name="compilation">The compilation the method is declared in.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    public static IReadOnlyDictionary<(SyntaxTree Tree, TextSpan Span), string> NestedBodyIds(
        IMethodSymbol method, Compilation compilation, CancellationToken cancellationToken)
    {
        var plan = Plan(method, compilation, cancellationToken);
        var ids = new Dictionary<(SyntaxTree, TextSpan), string>();
        var outerIds = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
        var lambdas = 0;
        foreach (var segment in plan.Segments.OfType<GraphSegment>())
        {
            var (localFunctions, anonymousFunctions, nestedIds) = AssignNestedIds(segment.Graph, plan.BodyId, outerIds, lambdas);
            CollectNestedBodyIds(segment.Graph, localFunctions, anonymousFunctions, nestedIds, ids, cancellationToken);
            outerIds = nestedIds;
            lambdas += anonymousFunctions.Length;
        }

        return ids;
    }

    private static void CollectNestedBodyIds(ControlFlowGraph graph, IMethodSymbol[] localFunctions,
                                             IFlowAnonymousFunctionOperation[] anonymousFunctions,
                                             Dictionary<IMethodSymbol, string> nestedIds,
                                             Dictionary<(SyntaxTree, TextSpan), string> ids, CancellationToken cancellationToken)
    {
        foreach (var localFunction in localFunctions)
        {
            if (localFunction.DeclaringSyntaxReferences.FirstOrDefault() is { } reference)
                ids[(reference.SyntaxTree, reference.Span)] = nestedIds[localFunction];
            var nestedGraph = graph.GetLocalFunctionControlFlowGraph(localFunction, cancellationToken);
            var nested = AssignNestedIds(nestedGraph, nestedIds[localFunction], nestedIds, 0);
            CollectNestedBodyIds(nestedGraph, nested.LocalFunctions, nested.AnonymousFunctions, nested.NestedIds, ids, cancellationToken);
        }

        foreach (var anonymousFunction in anonymousFunctions)
        {
            ids[(anonymousFunction.Syntax.SyntaxTree, anonymousFunction.Syntax.Span)] = nestedIds[anonymousFunction.Symbol];
            var nestedGraph = graph.GetAnonymousFunctionControlFlowGraph(anonymousFunction, cancellationToken);
            var nested = AssignNestedIds(nestedGraph, nestedIds[anonymousFunction.Symbol], nestedIds, 0);
            CollectNestedBodyIds(nestedGraph, nested.LocalFunctions, nested.AnonymousFunctions, nested.NestedIds, ids, cancellationToken);
        }
    }

    /// <summary>Assigns the ids of a graph's own lambdas and local functions; lambdas are numbered from
    /// <paramref name="lambdaOffset"/> + 1, so the graphs of one constructor body never repeat a number.</summary>
    /// <param name="graph">The control flow graph whose own lambdas and local functions are numbered.</param>
    /// <param name="bodyId">The id of the body the graph lowers to, which prefixes every nested id.</param>
    /// <param name="outerIds">The ids already assigned to enclosing nested functions, copied into the result.</param>
    /// <param name="lambdaOffset">The number of lambdas already numbered in earlier graphs of the same body.</param>
    private static (IMethodSymbol[] LocalFunctions, IFlowAnonymousFunctionOperation[] AnonymousFunctions,
                    Dictionary<IMethodSymbol, string> NestedIds) AssignNestedIds(
        ControlFlowGraph graph, string bodyId, IReadOnlyDictionary<IMethodSymbol, string> outerIds, int lambdaOffset)
    {
        var localFunctions = graph.LocalFunctions.ToArray();
        var localCounts = localFunctions.GroupBy(local => local.Name, StringComparer.Ordinal)
                                        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var localOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var nestedIds = new Dictionary<IMethodSymbol, string>(outerIds, SymbolEqualityComparer.Default);
        foreach (var localFunction in localFunctions)
        {
            var ordinal = localOrdinals.GetValueOrDefault(localFunction.Name) + 1;
            localOrdinals[localFunction.Name] = ordinal;
            var suffix = localCounts[localFunction.Name] == 1 ? "" : $"~{ordinal}";
            nestedIds.Add(localFunction, $"{bodyId}#local:{localFunction.Name}{suffix}");
        }

        var anonymousFunctions = AnonymousFunctions(graph).ToArray();
        for (var index = 0; index < anonymousFunctions.Length; index++)
            nestedIds.Add(anonymousFunctions[index].Symbol, $"{bodyId}#lambda{lambdaOffset + index + 1}");
        return (localFunctions, anonymousFunctions, nestedIds);
    }

    private static IrLoweredMethod LowerGraph(IMethodSymbol method, ControlFlowGraph graph, string bodyId,
                                              string ownerSymbol, bool hasReceiver,
                                              IReadOnlyDictionary<IMethodSymbol, string> outerIds, LoweringContext context)
    {
        var (localFunctions, anonymousFunctions, nestedIds) = AssignNestedIds(graph, bodyId, outerIds, 0);

        var body = new BodyLowerer(
            method,
            [new GraphSegment(graph, null)],
            bodyId,
            ownerSymbol,
            hasReceiver,
            nestedIds,
            context).Lower();
        return new IrLoweredMethod(body, LowerNestedFunctions(graph, localFunctions, anonymousFunctions, ownerSymbol, hasReceiver,
                                                              nestedIds, context));
    }

    private static List<IrBody> LowerNestedFunctions(ControlFlowGraph graph, IMethodSymbol[] localFunctions,
                                                     IFlowAnonymousFunctionOperation[] anonymousFunctions, string ownerSymbol,
                                                     bool hasReceiver, IReadOnlyDictionary<IMethodSymbol, string> nestedIds,
                                                     LoweringContext context)
    {
        var nestedBodies = new List<IrBody>();
        foreach (var localFunction in localFunctions)
        {
            var nested = LowerGraph(
                localFunction,
                graph.GetLocalFunctionControlFlowGraph(localFunction, context.CancellationToken),
                nestedIds[localFunction],
                ownerSymbol,
                hasReceiver,
                nestedIds,
                context);
            nestedBodies.Add(nested.Body);
            nestedBodies.AddRange(nested.NestedBodies);
        }

        foreach (var anonymousFunction in anonymousFunctions)
        {
            var nested = LowerGraph(
                anonymousFunction.Symbol,
                graph.GetAnonymousFunctionControlFlowGraph(anonymousFunction, context.CancellationToken),
                nestedIds[anonymousFunction.Symbol],
                ownerSymbol,
                hasReceiver,
                nestedIds,
                context);
            nestedBodies.Add(nested.Body);
            nestedBodies.AddRange(nested.NestedBodies);
        }

        return nestedBodies;
    }

    private static IEnumerable<IFlowAnonymousFunctionOperation> AnonymousFunctions(ControlFlowGraph graph)
    {
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var block in graph.Blocks)
        {
            foreach (var root in block.Operations.Append(block.BranchValue).Where(operation => operation is not null))
            {
                foreach (var anonymous in root!.DescendantsAndSelf().OfType<IFlowAnonymousFunctionOperation>())
                {
                    if (seen.Add(anonymous.Symbol))
                        yield return anonymous;
                }
            }
        }
    }

    /// <summary>The id of the method body that <paramref name="bodyId"/> is, or is nested in. Nested bodies append
    /// <c>#lambda&lt;n&gt;</c> or <c>#local:&lt;name&gt;</c> with an optional <c>~&lt;n&gt;</c>, composed; a <c>#</c>
    /// inside the documentation id itself, as in an explicit interface implementation, is part of the method id.</summary>
    /// <param name="bodyId">The body id to strip of its nested-body suffixes.</param>
    public static string EnclosingMethodBodyId(string bodyId) => NESTED_BODY_SUFFIX.Replace(bodyId, "");

    internal static string RootBodyId(IMethodSymbol method) =>
        $"body:{method.ContainingAssembly.Name}:{method.GetDocumentationCommentId() ?? method.ToDisplayString()}";

    /// <summary>A source property whose accessors all lack bodies and which is neither abstract, extern nor an interface
    /// member: its accessors have synthesized bodies over its backing field.</summary>
    /// <param name="property">The property to test.</param>
    /// <param name="cancellationToken">Cancels reading the declaring syntax.</param>
    internal static bool IsAutoProperty(IPropertySymbol property, CancellationToken cancellationToken) =>
        !property.IsAbstract && !property.IsExtern && property.ContainingType.TypeKind != TypeKind.Interface &&
        property.DeclaringSyntaxReferences.Length != 0 &&
        property.DeclaringSyntaxReferences.All(reference =>
            reference.GetSyntax(cancellationToken) is PropertyDeclarationSyntax { AccessorList: { Accessors.Count: > 0 } accessors } &&
            accessors.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null));

    /// <summary>A field as every access to it names it.</summary>
    /// <param name="field">The field symbol.</param>
    internal static IrFieldRef FieldRef(IFieldSymbol field) =>
        new(
            field.ContainingAssembly.Name,
            SymbolNames.Type(field.ContainingType),
            field.Name,
            IrFieldKind.Field,
            field.IsStatic,
            field.IsReadOnly,
            SymbolNames.Type(field.Type),
            SymbolNames.TypeIdentity(field.ContainingType))
        {
            IsVolatile = field.IsVolatile,
            IsContainingTypeReadOnly = field.ContainingType.IsReadOnly,
            FieldTypeKey = SymbolNames.TypeKey(field.Type)
        };

    /// <summary>The backing field of an automatic property as every access to it names it.</summary>
    /// <param name="property">The property symbol.</param>
    internal static IrFieldRef PropertyField(IPropertySymbol property) =>
        new(
            property.ContainingAssembly.Name,
            SymbolNames.Type(property.ContainingType),
            property.Name,
            IrFieldKind.PropertyBackingField,
            property.IsStatic,
            property.SetMethod is null,
            SymbolNames.Type(property.Type),
            SymbolNames.TypeIdentity(property.ContainingType)) { FieldTypeKey = SymbolNames.TypeKey(property.Type) };

    /// <summary>The storage of a captured primary constructor parameter as every access to it names it.</summary>
    /// <param name="parameter">The captured parameter symbol.</param>
    internal static IrFieldRef PrimaryConstructorParameterField(IParameterSymbol parameter) =>
        new(
            parameter.ContainingType.ContainingAssembly.Name,
            SymbolNames.Type(parameter.ContainingType),
            parameter.Name,
            IrFieldKind.PrimaryConstructorParameter,
            false,
            false,
            SymbolNames.Type(parameter.Type),
            SymbolNames.TypeIdentity(parameter.ContainingType)) { FieldTypeKey = SymbolNames.TypeKey(parameter.Type) };

    /// <summary>The field, field-like event and property initializers of <paramref name="type"/> with the member each initializes
    /// (an event's backing field for an event's), instance or static, ordered by file path (ordinal) and span start.</summary>
    /// <param name="type">The type whose declarations hold the initializers.</param>
    /// <param name="isStatic">Whether to collect the static initializers rather than the instance ones.</param>
    /// <param name="compilation">The compilation the type is declared in.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    internal static IReadOnlyList<(EqualsValueClauseSyntax Clause, ISymbol Member)> Initializers(
        INamedTypeSymbol type, bool isStatic, Compilation compilation, CancellationToken cancellationToken)
    {
        var initializers = new List<(EqualsValueClauseSyntax, ISymbol)>();
        foreach (var declaration in type.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(cancellationToken))
                                        .OfType<TypeDeclarationSyntax>())
        {
            var model = compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (var member in declaration.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                        foreach (var variable in field.Declaration.Variables)
                        {
                            if (variable.Initializer is not null &&
                                model.GetDeclaredSymbol(variable, cancellationToken) is IFieldSymbol { IsConst: false } symbol &&
                                symbol.IsStatic == isStatic)
                            {
                                initializers.Add((variable.Initializer, symbol));
                            }
                        }
                        break;
                    case PropertyDeclarationSyntax { Initializer: { } initializer } property
                        when model.GetDeclaredSymbol(property, cancellationToken) is { } symbol && symbol.IsStatic == isStatic:
                        initializers.Add((initializer, symbol));
                        break;
                    // A field-like event's initializer stores into the field the compiler declares for it (ADR 0014), whose
                    // initializer it is.
                    case EventFieldDeclarationSyntax @event:
                        foreach (var variable in @event.Declaration.Variables)
                        {
                            if (variable.Initializer is not null &&
                                model.GetDeclaredSymbol(variable, cancellationToken) is IEventSymbol symbol && symbol.IsStatic == isStatic)
                            {
                                var owner = model.GetOperation(variable.Initializer, cancellationToken) is IFieldInitializerOperation { InitializedFields: [var field] }
                                    ? field
                                    : (ISymbol)symbol;
                                initializers.Add((variable.Initializer, owner));
                            }
                        }
                        break;
                }
            }
        }

        return initializers.OrderBy(initializer => initializer.Item1.SyntaxTree.FilePath, StringComparer.Ordinal)
                           .ThenBy(initializer => initializer.Item1.SpanStart)
                           .ToArray();
    }

    private static MemberPlan Plan(IMethodSymbol method, Compilation compilation, CancellationToken cancellationToken)
    {
        var segments = new List<Segment>();
        var roots = new List<SyntaxNode>();
        var declaration = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken);
        var type = method.ContainingType;

        void AddGraph(SyntaxNode node, ISymbol? initializerOwner)
        {
            var graph = ControlFlowGraph.Create(node, compilation.GetSemanticModel(node.SyntaxTree), cancellationToken)
                        ?? throw new ArgumentException("The method has no source body.", nameof(method));
            segments.Add(new GraphSegment(graph, initializerOwner));
            roots.Add(node);
        }

        void AddInitializers(bool isStatic)
        {
            foreach (var (clause, member) in Initializers(type, isStatic, compilation, cancellationToken))
                AddGraph(clause, member);
        }

        void AddImplicitBaseCall(SyntaxNode syntax)
        {
            var constructor = type is { TypeKind: TypeKind.Class, BaseType: { } baseType }
                ? baseType.InstanceConstructors.Where(candidate => candidate.Parameters.All(parameter => parameter.IsOptional || parameter.IsParams))
                          .OrderBy(candidate => candidate.Parameters.Length)
                          .FirstOrDefault()
                : null;
            if (constructor is not null)
                segments.Add(new ImplicitBaseCallSegment(constructor, syntax));
        }

        switch (method.MethodKind)
        {
            case MethodKind.Constructor when declaration is ConstructorDeclarationSyntax constructor:
                if (constructor.Initializer?.IsKind(SyntaxKind.ThisConstructorInitializer) != true)
                    AddInitializers(false);
                AddGraph(constructor, null);
                break;
            case MethodKind.Constructor when declaration is TypeDeclarationSyntax primary:
                var captured = CapturedPrimaryConstructorParameters(type, method, compilation, cancellationToken);
                if (captured.Count != 0)
                    segments.Add(new PrimaryConstructorParametersSegment(captured));
                AddInitializers(false);
                if (compilation.GetSemanticModel(primary.SyntaxTree).GetOperation(primary, cancellationToken) is not null)
                    AddGraph(primary, null);
                else
                    AddImplicitBaseCall(primary);
                break;
            case MethodKind.Constructor when declaration is null && method.IsImplicitlyDeclared && method.Parameters.Length == 0 &&
                                            FirstDeclaration(type, cancellationToken) is { } typeDeclaration:
                AddInitializers(false);
                AddImplicitBaseCall(typeDeclaration);
                break;
            case MethodKind.StaticConstructor when type.DeclaringSyntaxReferences.Length != 0:
                AddInitializers(true);
                if (declaration is not null)
                    AddGraph(declaration, null);
                break;
            case MethodKind.PropertyGet or MethodKind.PropertySet
                when method.AssociatedSymbol is IPropertySymbol property && IsAutoProperty(property, cancellationToken):
                segments.Add(new AutoAccessorSegment(property, declaration ?? property.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken)));
                break;
            case MethodKind.EventAdd or MethodKind.EventRemove
                when method.AssociatedSymbol is IEventSymbol @event && FieldLikeEvents.Is(@event):
                segments.Add(new FieldLikeEventAccessorSegment(@event, declaration ?? @event.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken)));
                break;
            default:
                if (declaration is null)
                    throw new ArgumentException("The method has no source body.", nameof(method));
                AddGraph(declaration, null);
                break;
        }

        // Only an implicit struct constructor without initializers has an empty body.
        if (segments.Count == 0 && method.MethodKind != MethodKind.Constructor)
            throw new ArgumentException("The method has no source body.", nameof(method));
        return new MemberPlan(RootBodyId(method), segments, roots);
    }

    private static SyntaxNode? FirstDeclaration(INamedTypeSymbol type, CancellationToken cancellationToken) =>
        type.DeclaringSyntaxReferences.OrderBy(reference => reference.SyntaxTree.FilePath, StringComparer.Ordinal)
            .ThenBy(reference => reference.Span.Start)
            .FirstOrDefault()?.GetSyntax(cancellationToken);

    /// <summary>The primary constructor parameters the type captures: those referenced outside its initializers and the
    /// primary constructor's base argument list. A reference inside a lambda or local function an initializer declares is a
    /// capture too: that nested body reads the parameter through the type, so the constructor must store it there.</summary>
    /// <param name="type">The type whose declarations are searched for references.</param>
    /// <param name="constructor">The primary constructor whose parameters are tested.</param>
    /// <param name="compilation">The compilation the type is declared in.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    internal static IReadOnlyList<IParameterSymbol> CapturedPrimaryConstructorParameters(INamedTypeSymbol type, IMethodSymbol constructor,
                                                                                      Compilation compilation,
                                                                                      CancellationToken cancellationToken)
    {
        var declarations = type.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(cancellationToken))
                               .OfType<TypeDeclarationSyntax>()
                               .ToArray();
        return constructor.Parameters.Where(parameter => declarations.Any(declaration =>
        {
            var model = compilation.GetSemanticModel(declaration.SyntaxTree);
            return declaration.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier =>
                identifier.Identifier.ValueText == parameter.Name &&
                !identifier.Ancestors().Any(ancestor => ancestor is PrimaryConstructorBaseTypeSyntax) &&
                !IsDirectlyInInitializer(identifier) &&
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, parameter));
        })).ToArray();
    }

    /// <summary>Whether a reference sits in a field, field-like event or property initializer and not in a lambda or local function
    /// that initializer declares; the constructor runs such a reference itself, so it is not a capture.</summary>
    /// <param name="reference">The referencing syntax.</param>
    private static bool IsDirectlyInInitializer(SyntaxNode reference)
    {
        foreach (var ancestor in reference.Ancestors())
        {
            if (ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                return false;
            if (ancestor is EqualsValueClauseSyntax
                {
                    Parent: VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax } or PropertyDeclarationSyntax
                })
            {
                return true;
            }
        }

        return false;
    }

    private sealed record MemberPlan(string BodyId, IReadOnlyList<Segment> Segments, IReadOnlyList<SyntaxNode> Roots);

    private abstract record Segment;

    private sealed record GraphSegment(ControlFlowGraph Graph, ISymbol? InitializerOwner) : Segment;

    private sealed record PrimaryConstructorParametersSegment(IReadOnlyList<IParameterSymbol> Parameters) : Segment;

    private sealed record ImplicitBaseCallSegment(IMethodSymbol Constructor, SyntaxNode Syntax) : Segment;

    private sealed record AutoAccessorSegment(IPropertySymbol Property, SyntaxNode Syntax) : Segment;

    /// <summary>The compiler's body of a field-like event's add or remove accessor (ADR 0014).</summary>
    /// <param name="Event">The field-like event.</param>
    /// <param name="Syntax">The syntax the body's operations are attributed to.</param>
    private sealed record FieldLikeEventAccessorSegment(IEventSymbol Event, SyntaxNode Syntax) : Segment;

    private sealed record LoweringContext(string RootBodyId, SiteOrdinals SiteOrdinals, string RootDirectory,
                                          Compilation Compilation, CancellationToken CancellationToken, LibraryModels LibraryModels)
    {
        internal Dictionary<string, IReadOnlySet<string>> MetadataSupertypes { get; } = new(StringComparer.Ordinal);

        internal void RecordMetadataSupertypes(ITypeSymbol? type)
        {
            if (type is null || type is not IArrayTypeSymbol &&
                SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, Compilation.Assembly))
                return;

            var supertypes = new HashSet<string>(StringComparer.Ordinal);
            for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
                supertypes.Add(TypeSafety.DefinitionOf(SymbolNames.TypeKey(current)));
            foreach (var @interface in type.AllInterfaces)
                supertypes.Add(TypeSafety.DefinitionOf(SymbolNames.TypeKey(@interface)));
            MetadataSupertypes[TypeSafety.DefinitionOf(SymbolNames.TypeKey(type))] = supertypes;
        }
    }

    /// <summary>The 1-based source order of each allocation among those of its created type, and of each delegate creation,
    /// within one member: its roots in segment order, then span start, then operation-tree order.</summary>
    private sealed class SiteOrdinals
    {
        private readonly Dictionary<(SyntaxTree Tree, TextSpan Span, OperationKind Kind), int> _ordinals = [];

        internal SiteOrdinals(IEnumerable<SyntaxNode> roots, Compilation compilation, CancellationToken cancellationToken)
        {
            var allocations = new Dictionary<string, int>(StringComparer.Ordinal);
            var delegates = 0;
            foreach (var root in roots)
            {
                var operation = compilation.GetSemanticModel(root.SyntaxTree).GetOperation(root, cancellationToken);
                if (operation is null)
                    continue;

                var ordered = operation.DescendantsAndSelf()
                                       .Select((descendant, index) => (Operation: descendant, Index: index))
                                       .OrderBy(item => item.Operation.Syntax.SpanStart)
                                       .ThenBy(item => item.Index)
                                       .Select(item => item.Operation);
                foreach (var descendant in ordered)
                {
                    switch (descendant)
                    {
                        case IObjectCreationOperation or IArrayCreationOperation when descendant.Type is not null:
                            var typeKey = SymbolNames.TypeKey(descendant.Type!);
                            var ordinal = allocations.GetValueOrDefault(typeKey) + 1;
                            allocations[typeKey] = ordinal;
                            _ordinals.TryAdd(Key(descendant), ordinal);
                            break;
                        case IDelegateCreationOperation:
                            _ordinals.TryAdd(Key(descendant), ++delegates);
                            break;
                    }
                }
            }
        }

        internal int Of(IOperation operation) => _ordinals.GetValueOrDefault(Key(operation));

        private static (SyntaxTree, TextSpan, OperationKind) Key(IOperation operation) =>
            (operation.Syntax.SyntaxTree, operation.Syntax.Span, operation.Kind);
    }

    private sealed class BodyLowerer
    {
        private readonly IMethodSymbol _method;
        private readonly IReadOnlyList<Segment> _segments;
        private readonly string _bodyId;
        private readonly string _ownerSymbol;
        private readonly bool _hasReceiver;
        private readonly IReadOnlyDictionary<IMethodSymbol, string> _nestedIds;
        private readonly LoweringContext _context;
        private readonly string _rootDirectory;
        private readonly CancellationToken _cancellationToken;
        private readonly List<IrValue> _values = [];
        private readonly List<IrRegion> _regions = [];
        private readonly Dictionary<ControlFlowRegion, int> _regionIds = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ISymbol, int> _symbolValues = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ISymbol, int> _symbolVersions = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<SsaVariable, int> _ssaVersions = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<CaptureId, int> _captureValues = [];
        private readonly Dictionary<SsaToken, int> _tokenValues = [];
        private readonly Dictionary<SsaVariable, int> _definitionPositions = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(SyntaxTree Tree, int Start, int Length), int> _coalesceLoads = [];
        private readonly Dictionary<CaptureId, IOperation> _capturedTargets = [];

        /// <summary>The write of each captured assignable target, through the receiver and indices its capture evaluated.</summary>
        private readonly Dictionary<CaptureId, Action<int, IOperation, string>> _capturedLongForms = [];
        private readonly Dictionary<IParameterSymbol, int> _parameterValues = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<IOperation, IReadOnlyList<int>> _listedElements = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, int> _configuredTasks = [];

        /// <summary>The scope each <c>EnterScope</c> handed back, by its value: disposing it leaves the lock.</summary>
        private readonly Dictionary<int, LockScope> _lockScopes = [];

        /// <summary>The entry each asynchronous wait would make, by the value awaiting it completes.</summary>
        private readonly Dictionary<int, PendingEntry> _pendingEntries = [];

        /// <summary>The lock sections of this body, found once; and the value each one that has been entered was entered on.</summary>
        private List<LockSectionInfo>? _lockSections;

        private readonly Dictionary<LockStatementSyntax, int> _openLocks = [];

        /// <summary>The sections already left in the block being lowered, so that one is never left twice.</summary>
        private readonly HashSet<LockStatementSyntax> _closedHere = [];
        private ControlFlowGraph _graph = null!;
        private EffectiveFlowGraph _flowGraph = null!;
        private SsaPlan _ssaPlan = null!;
        private int _offset;
        private List<IrOperation> _operations = [];
        private int _currentBlockOrdinal;
        private int? _receiverValue;
        private int? _patternInputValue;
        private int _nextValueId;
        private int _nextOperationId;

        internal BodyLowerer(IMethodSymbol method, IReadOnlyList<Segment> segments, string bodyId, string ownerSymbol,
                             bool hasReceiver, IReadOnlyDictionary<IMethodSymbol, string> nestedIds, LoweringContext context)
        {
            _method = method;
            _segments = segments;
            _bodyId = bodyId;
            _ownerSymbol = ownerSymbol;
            _hasReceiver = hasReceiver;
            _nestedIds = nestedIds;
            _context = context;
            _rootDirectory = Path.GetFullPath(context.RootDirectory);
            _cancellationToken = context.CancellationToken;
        }

        internal IrBody Lower()
        {
            var blocks = MarkComposite(MarkAwaited(_segments is [GraphSegment only] ? LowerSingleGraph(only) : LowerSegments()));
            var body = new IrBody(
                _bodyId,
                BodyKind(_method),
                _ownerSymbol,
                SymbolNames.Method(_method),
                _values,
                blocks,
                _regions)
            {
                Parameters = _method.Parameters
                                    .Select(parameter => new IrParameter(parameter.Name, TypeName(parameter.Type), RefKindOf(parameter.RefKind),
                                                                         parameter.Ordinal, _parameterValues[parameter]))
                                    .ToArray(),
                IsAsync = _method.IsAsync,
                ReturnType = TypeName(_method.ReturnType),
                IsAsyncIterator = _method is { IsAsync: true, IsIterator: true },
                IsIterator = _method is { IsAsync: false, IsIterator: true }
            };
            var problems = IrValidator.Validate(body);
            if (problems.Count != 0)
                throw new InvalidOperationException("Lowered IR is invalid: " + string.Join("; ", problems));
            return body;
        }

        /// <summary>Marks the calls whose result an await takes with nothing of the body in between. A conditional, a <c>??</c> or a
        /// <c>switch</c> expression reaches its await through the assignments and phis the control-flow graph introduces for it, over
        /// temporaries of its own; a task the body keeps in a local of its own does not, since it may do anything before awaiting it.</summary>
        /// <param name="blocks">The lowered blocks of the body.</param>
        private IrBlock[] MarkAwaited(IrBlock[] blocks)
        {
            var operations = blocks.SelectMany(block => block.Operations).ToArray();
            var definitions = new Dictionary<int, IrOperation>();
            foreach (var operation in operations)
            {
                foreach (var value in operation.DefinedValues)
                    definitions[value] = operation;
            }

            var awaited = new HashSet<int>();
            foreach (var await in operations.OfType<IrAwaitOperation>())
            {
                var seen = new HashSet<int>();
                var pending = new Stack<int>([await.TaskValue ?? await.AwaitableValue]);
                while (pending.TryPop(out var value))
                {
                    if (!seen.Add(value) || !definitions.TryGetValue(value, out var definition) ||
                        _values[value].Kind != IrValueKind.Temporary)
                    {
                        continue;
                    }

                    switch (definition)
                    {
                        case IrCallOperation call:
                            awaited.Add(call.Id);
                            break;
                        case IrAssignOperation assign:
                            pending.Push(assign.SourceValue);
                            break;
                        case IrPhiOperation phi:
                            foreach (var input in phi.Inputs)
                                pending.Push(input.Value);
                            break;
                    }
                }
            }

            return awaited.Count == 0
                ? blocks
                : blocks.Select(block => block with
                        {
                            Operations = block.Operations
                                              .Select(operation => operation is IrCallOperation call && awaited.Contains(call.Id)
                                                  ? call with { IsAwaitedImmediately = true }
                                                  : operation)
                                              .ToArray()
                        })
                        .ToArray();
        }

        /// <summary>Turns a continuation of a composite task into the unrecognized form (R7): the plan names <c>ContinueWith</c> on the
        /// task of one spawn, not on the task <c>WhenAll</c>, another unrecognized form or an <c>Unwrap</c> of one built, so such a
        /// continuation runs as work that overlaps everything and its completion gives no order.</summary>
        /// <param name="blocks">The lowered blocks of the body.</param>
        private static IrBlock[] MarkComposite(IrBlock[] blocks)
        {
            var operations = blocks.SelectMany(block => block.Operations).ToArray();
            var continuations = operations.OfType<IrSpawnOperation>()
                                          .Where(spawn => spawn.Kind == IrSpawnKind.ContinueWith && spawn.AntecedentValue is not null)
                                          .ToArray();
            if (continuations.Length == 0)
                return blocks;

            var whenAlls = operations.OfType<IrWhenAllOperation>().Select(whenAll => whenAll.ResultValue).ToHashSet();
            var unwraps = new Dictionary<int, int>();
            foreach (var unwrap in operations.OfType<IrUnwrapOperation>())
                unwraps[unwrap.ResultValue] = unwrap.OuterValue;
            var spawns = new Dictionary<int, IrSpawnOperation>();
            foreach (var spawn in operations.OfType<IrSpawnOperation>().Where(spawn => spawn.HandleValue is not null))
                spawns[spawn.HandleValue!.Value] = spawn;
            var sources = new Dictionary<int, List<int>>();
            foreach (var operation in operations)
            {
                switch (operation)
                {
                    case IrAssignOperation assign:
                        Get(sources, assign.TargetValue).Add(assign.SourceValue);
                        break;
                    case IrPhiOperation phi:
                        Get(sources, phi.TargetValue).AddRange(phi.Inputs.Select(input => input.Value));
                        break;
                }
            }

            var composite = new HashSet<int>();
            bool Composite(int value)
            {
                var seen = new HashSet<int>();
                var pending = new Stack<int>([value]);
                while (pending.TryPop(out var current))
                {
                    if (!seen.Add(current))
                        continue;
                    if (whenAlls.Contains(current))
                        return true;
                    if (unwraps.TryGetValue(current, out var outer))
                        pending.Push(outer);
                    if (spawns.TryGetValue(current, out var spawn) && (spawn.Kind == IrSpawnKind.Unrecognized || composite.Contains(spawn.Id)))
                        return true;
                    foreach (var source in sources.GetValueOrDefault(current) ?? [])
                        pending.Push(source);
                }

                return false;
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var continuation in continuations.Where(continuation => !composite.Contains(continuation.Id)))
                {
                    if (!Composite(continuation.AntecedentValue!.Value))
                        continue;
                    composite.Add(continuation.Id);
                    changed = true;
                }
            }

            return composite.Count == 0
                ? blocks
                : blocks.Select(block => block with
                        {
                            Operations = block.Operations
                                              .Select(operation => operation is IrSpawnOperation spawn && composite.Contains(spawn.Id)
                                                  ? spawn with { Kind = IrSpawnKind.Unrecognized, HandleValue = null, AntecedentValue = null }
                                                  : operation)
                                              .ToArray()
                        })
                        .ToArray();
        }

        private static List<int> Get(Dictionary<int, List<int>> map, int key)
        {
            if (!map.TryGetValue(key, out var values))
                map.Add(key, values = []);
            return values;
        }

        private IrBlock[] LowerSingleGraph(GraphSegment segment)
        {
            EnterSegment(segment, 0);
            AddInitialValues();
            AllocateSsaValues();
            AddRegion(_graph.Root, null);
            return _graph.Blocks.Select(LowerBlock).ToArray();
        }

        /// <summary>A body of several graphs and synthesized steps, run in order: a synthesized entry, each graph with its entry and
        /// exit turned into ordinary blocks, each synthesized step as one block, and a synthesized exit. Every variable enters a
        /// graph with the value it had when the previous graph ended.</summary>
        private IrBlock[] LowerSegments()
        {
            if (_hasReceiver)
                _receiverValue = AddValue(IrValueKind.Receiver, _method.ContainingType, "this", 0, THIS_KEY);
            var current = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
            foreach (var parameter in _method.Parameters)
            {
                var value = AddValue(IrValueKind.Parameter, parameter.Type, parameter.Name, 0, SymbolKey(parameter));
                _parameterValues[parameter] = value;
                current[parameter] = value;
            }

            var count = 2 + _segments.Sum(segment => segment is GraphSegment graph ? graph.Graph.Blocks.Length : 1);
            _regions.Add(new IrRegion(0, IrRegionKind.Root, null, 0, count - 1, null));
            var blocks = new List<IrBlock> { new(0, IrBlockKind.Entry, 0, [], [], [], null, Jump(IrBranchKind.Regular, 1)) };
            foreach (var segment in _segments)
            {
                var offset = blocks.Count;
                var entry = new IrFlowPredecessor(offset - 1, IrEdgeKind.Explicit);
                if (segment is GraphSegment graphSegment)
                {
                    EnterSegment(graphSegment, offset);
                    _regionIds[_graph.Root] = 0;
                    foreach (var nested in _graph.Root.NestedRegions)
                        AddRegion(nested, 0);
                    foreach (var variable in _ssaPlan.Variables.Where(variable => variable.IsIncoming))
                    {
                        var value = variable.Symbol is { } symbol && current.TryGetValue(symbol, out var carried)
                            ? carried
                            : AddValue(variable.Kind, variable.Type, variable.Name, 0, KeyOf(variable));
                        _tokenValues.Add(variable.Incoming, value);
                    }

                    AllocateSsaValues();
                    var lowered = _graph.Blocks.Select(LowerBlock).ToArray();
                    lowered[0] = lowered[0] with { Kind = IrBlockKind.Block, Predecessors = [offset - 1], FlowPredecessors = [entry] };
                    lowered[^1] = lowered[^1] with
                    {
                        Kind = IrBlockKind.Block,
                        FallThroughBranch = Jump(IrBranchKind.Regular, offset + lowered.Length)
                    };
                    blocks.AddRange(lowered);
                    var exit = _graph.Blocks[^1].Ordinal;
                    foreach (var variable in _ssaPlan.Variables.Where(variable => variable.Symbol is not null))
                        current[variable.Symbol!] = ResolveToken(_ssaPlan.Entry(exit, variable));
                }
                else
                {
                    _operations = [];
                    var branch = LowerSynthesized(segment, current);
                    blocks.Add(new IrBlock(offset, IrBlockKind.Block, 0, [offset - 1], [entry], _operations, null, Jump(branch, offset + 1)));
                }
            }

            blocks.Add(new IrBlock(count - 1, IrBlockKind.Exit, 0, [count - 2], [new IrFlowPredecessor(count - 2, IrEdgeKind.Explicit)],
                                   [], null, null));
            return blocks.ToArray();
        }

        private IrBranchKind LowerSynthesized(Segment segment, IReadOnlyDictionary<ISymbol, int> current)
        {
            switch (segment)
            {
                case PrimaryConstructorParametersSegment primary:
                    foreach (var parameter in primary.Parameters)
                    {
                        var location = GetPrimaryConstructorParameterLocation(parameter);
                        var syntax = parameter.DeclaringSyntaxReferences[0].GetSyntax(_cancellationToken);
                        _operations.Add(new IrStoreFieldOperation(
                            NextOperation(), location.Receiver, location.Field, current[parameter], null,
                            Provenance(syntax, "primary-constructor-parameter")));
                    }
                    return IrBranchKind.Regular;
                case ImplicitBaseCallSegment baseCall:
                    _operations.Add(Call(null, baseCall.Constructor, _receiverValue, LoweredArguments.None,
                                         Provenance(baseCall.Syntax, "implicit-base-constructor")));
                    return IrBranchKind.Regular;
                case AutoAccessorSegment accessor:
                {
                    var field = PropertyField(accessor.Property);
                    var provenance = Provenance(accessor.Syntax, "property-backing-field");
                    if (_method.MethodKind == MethodKind.PropertyGet)
                    {
                        var result = AddTemporary(accessor.Property.Type);
                        _operations.Add(new IrLoadFieldOperation(NextOperation(), result, _receiverValue, field, provenance));
                        _operations.Add(new IrReturnOperation(NextOperation(), result, Provenance(accessor.Syntax, "return")));
                        return IrBranchKind.Return;
                    }

                    _operations.Add(new IrStoreFieldOperation(NextOperation(), _receiverValue, field, current[_method.Parameters[^1]], null,
                                                              provenance));
                    return IrBranchKind.Regular;
                }
                // The compiler's accessor reads the field, combines the handler into it or removes the handler from it, and stores the
                // result with a compare-and-swap against the value it read, retrying until none came between (ADR 0014).
                case FieldLikeEventAccessorSegment accessor:
                {
                    var field = FieldLikeEvents.FieldRef(accessor.Event);
                    var provenance = Provenance(accessor.Syntax, "field-like-event");
                    var loaded = AddTemporary(accessor.Event.Type);
                    var loadId = NextOperation();
                    _operations.Add(new IrLoadFieldOperation(loadId, loaded, _receiverValue, field, provenance));
                    var combined = CombineDelegates(_method.MethodKind == MethodKind.EventRemove, [loaded, current[_method.Parameters[^1]]],
                                                    accessor.Event.Type, provenance);
                    var storeId = NextOperation();
                    _operations.Add(new IrStoreFieldOperation(storeId, _receiverValue, field, combined, loadId, provenance));
                    _operations.Add(Atomic(storeId, IrAtomicEffect.CompareAndSwap, "Interlocked.CompareExchange", null, provenance, loaded));
                    return IrBranchKind.Regular;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(segment), segment.GetType().FullName);
            }
        }

        private static IrBranch Jump(IrBranchKind kind, int destination) => new(kind, destination, null, null, [], [], []);

        private void EnterSegment(GraphSegment segment, int offset)
        {
            _graph = segment.Graph;
            _flowGraph = EffectiveFlowGraph.Create(_graph);
            _ssaPlan = SsaPlan.Create(_method, _graph, _flowGraph, segment.InitializerOwner);
            _offset = offset;
            _capturedTargets.Clear();
            _capturedLongForms.Clear();
        }

        private void AddInitialValues()
        {
            if (_hasReceiver)
                _receiverValue = AddValue(IrValueKind.Receiver, _method.ContainingType, "this", 0, THIS_KEY);

            foreach (var variable in _ssaPlan.Variables.Where(variable => variable.IsIncoming))
            {
                var value = AddValue(variable.Kind, variable.Type, variable.Name, 0, KeyOf(variable));
                _tokenValues.Add(variable.Incoming, value);
                if (variable.Symbol is IParameterSymbol parameter && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, _method))
                    _parameterValues[parameter] = value;
            }
        }

        private void AllocateSsaValues()
        {
            foreach (var block in _graph.Blocks)
            {
                foreach (var (_, token) in _ssaPlan.Phis(block.Ordinal))
                    ResolveToken(token);
                foreach (var definition in _ssaPlan.Definitions(block.Ordinal))
                    ResolveToken(definition.Token);
            }
        }

        private void AddRegion(ControlFlowRegion region, int? parent)
        {
            var id = _regions.Count;
            _regionIds.Add(region, id);
            _regions.Add(new IrRegion(
                id,
                RegionKind(region.Kind),
                parent,
                region.FirstBlockOrdinal + _offset,
                region.LastBlockOrdinal + _offset,
                region.ExceptionType is null ? null : SymbolNames.Type(region.ExceptionType)));
            foreach (var nested in region.NestedRegions)
                AddRegion(nested, id);
        }

        private IrBlock LowerBlock(BasicBlock block)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _operations = [];
            _currentBlockOrdinal = block.Ordinal;
            _definitionPositions.Clear();
            _symbolValues.Clear();
            _captureValues.Clear();
            foreach (var variable in _ssaPlan.Variables)
                SetCurrent(variable, ResolveToken(_ssaPlan.Entry(block.Ordinal, variable)));

            foreach (var (variable, token) in _ssaPlan.Phis(block.Ordinal))
            {
                var inputs = _ssaPlan.PhiInputs(block.Ordinal, variable)
                    .Select(input => new IrPhiInput(
                        new IrFlowPredecessor(input.Edge.Source + _offset, input.Edge.Kind),
                        ResolveToken(input.Token)))
                    .ToArray();
                var merged = ResolveToken(token);
                _operations.Add(new IrPhiOperation(
                    NextOperation(), merged, inputs,
                    Provenance(block.Operations.FirstOrDefault() ?? block.BranchValue ?? _graph.OriginalOperation, "phi")));
                // A value merged from paths that all carry the same meaning carries it too; where they disagree it means
                // nothing, and nothing is what a reader of the merged value then finds.
                Merge(merged, inputs.Select(input => input.Value).ToArray());
            }

            if (block.Ordinal == 0)
            {
                foreach (var variable in _ssaPlan.CapturedVariables)
                {
                    _operations.Add(new IrCaptureOperation(
                        NextOperation(), ResolveToken(variable.Incoming), _bodyId,
                        Provenance(variable.FirstReference ?? _graph.OriginalOperation, "capture"))
                    {
                        SymbolKey = KeyOf(variable)
                    });
                }
            }

            LowerOperations(block);

            var byRefReturn = _method.ReturnsByRef || _method.ReturnsByRefReadonly;
            int? branchValue = block.BranchValue is null ? null
                : byRefReturn && block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return
                    ? LowerAddress(block.BranchValue) : LowerValue(block.BranchValue);
            // The section's exit stands after everything the block runs inside it, the condition it leaves on included.
            LowerLockExits(block);
            if (block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
            {
                _operations.Add(new IrReturnOperation(
                    NextOperation(),
                    branchValue,
                    Provenance(block.BranchValue ?? block.Operations.LastOrDefault() ?? _graph.OriginalOperation, "return"))
                {
                    IsByRef = byRefReturn
                });
            }

            var predecessors = block.Predecessors.Select(predecessor => predecessor.Source.Ordinal + _offset).ToArray();
            var flowPredecessors = _flowGraph.Predecessors(block.Ordinal)
                                                   .Select(edge => new IrFlowPredecessor(edge.Source + _offset, edge.Kind))
                                                   .ToArray();
            return new IrBlock(
                block.Ordinal + _offset,
                BlockKind(block.Kind),
                _regionIds[block.EnclosingRegion],
                predecessors,
                flowPredecessors,
                _operations,
                LowerBranch(block.ConditionalSuccessor, branchValue,
                    block.ConditionKind == ControlFlowConditionKind.WhenTrue),
                LowerBranch(block.FallThroughSuccessor, null, null));
        }

        private IrBranch? LowerBranch(ControlFlowBranch? branch, int? condition, bool? jumpIfTrue)
        {
            if (branch is null)
                return null;

            return new IrBranch(
                BranchKind(branch.Semantics),
                branch.Destination?.Ordinal + _offset,
                condition,
                jumpIfTrue,
                branch.EnteringRegions.Select(region => _regionIds[region]).ToArray(),
                branch.LeavingRegions.Select(region => _regionIds[region]).ToArray(),
                branch.FinallyRegions.Select(region => _regionIds[region]).ToArray());
        }

        /// <summary>
        /// The operations of one block, with the entry of a <c>lock</c> statement the compiler leaves unlowered put in front of
        /// its body. A <c>lock</c> over a <c>System.Threading.Lock</c> is not rewritten into an enter and an exit the way a
        /// <c>lock</c> over an object is: the control flow graph keeps one operation whose syntax is the statement itself, lays
        /// the body out after it as ordinary operations, and marks the section with no region at all. The statement's own span
        /// is what the region would have been, so the section is every block the body's operations fall in, however the compiler
        /// spread them (TD-080).
        /// </summary>
        /// <param name="block">The control flow graph block whose operations are lowered.</param>
        private void LowerOperations(BasicBlock block)
        {
            _closedHere.Clear();
            var sections = LockSections().Where(candidate => candidate.Blocks.Contains(block.Ordinal)).ToArray();
            for (var index = 0; index < block.Operations.Length; index++)
            {
                var operation = block.Operations[index];
                if (LockSectionOf(operation) is { } opened)
                {
                    var value = LowerValue(opened.Gate);
                    _openLocks[opened.Statement] = value;
                    _operations.Add(new IrAcquireOperation(NextOperation(), value, IrSynchronizationPrimitive.Lock,
                                                           IrLockMode.Exclusive, Provenance(operation, "lock-statement")));
                    // A body with nothing in it is left where it was entered: the section covers this block alone and nothing
                    // after the statement here belongs to it.
                    if (opened.Blocks.Count == 1 && !CoversAfter(opened, block, index))
                        Release(opened, operation);
                    continue;
                }

                LowerTop(operation);

                // A section whose span ends inside this block is left here and not at the block's end: whatever the block runs
                // after the statement is outside it, and holding the lock over that would protect what nothing protects.
                foreach (var section in sections.Where(candidate => Covers(candidate, operation) &&
                                                                    !CoversAfter(candidate, block, index))
                                                .OrderBy(candidate => candidate.Statement.Statement.Span.Length))
                {
                    Release(section, operation);
                }
            }
        }

        private static bool Covers(LockSectionInfo section, IOperation operation) =>
            section.Statement.Statement.Span.Contains(operation.Syntax.Span);

        /// <summary>Whether anything this block runs after <paramref name="index"/>, its branch condition included, is still
        /// inside the section.</summary>
        /// <param name="section">The <c>lock</c> section to test.</param>
        /// <param name="block">The block being lowered.</param>
        /// <param name="index">The index of the operation in the block after which to look.</param>
        private static bool CoversAfter(LockSectionInfo section, BasicBlock block, int index) =>
            block.Operations.Skip(index + 1).Concat(block.BranchValue is { } value ? [value] : [])
                 .Any(later => Covers(section, later));

        private void Release(LockSectionInfo section, IOperation at)
        {
            if (!_openLocks.TryGetValue(section.Statement, out var value) || !_closedHere.Add(section.Statement))
                return;

            _operations.Add(new IrReleaseOperation(NextOperation(), value, IrSynchronizationPrimitive.Lock,
                                                   IrLockMode.Exclusive, Provenance(at, "lock-statement")));
        }

        /// <summary>
        /// The exits of the <c>lock</c> sections this block is the last block of, appended once its own operations are lowered.
        /// A section ends on every edge that leaves the blocks its body covers — the fall-through past the statement, a branch
        /// out of it, a <c>return</c> inside it — which is what the language guarantees and what the missing region would have
        /// said. The innermost section is left first, so nesting unwinds the way it was entered (R3).
        /// </summary>
        /// <param name="block">The control flow graph block just lowered.</param>
        private void LowerLockExits(BasicBlock block)
        {
            foreach (var section in LockSections().Where(candidate => candidate.Blocks.Contains(block.Ordinal))
                                                  .OrderBy(candidate => candidate.Statement.Statement.Span.Length))
            {
                if (LeavesSection(block, section))
                    Release(section, block.Operations.LastOrDefault() ?? _graph.OriginalOperation);
            }
        }

        /// <summary>Whether control leaves a section's blocks from this one: a successor outside them, or no successor at all.</summary>
        /// <param name="block">The block whose successors are checked.</param>
        /// <param name="section">The <c>lock</c> section whose blocks are tested.</param>
        private static bool LeavesSection(BasicBlock block, LockSectionInfo section) =>
            Successors(block).Any(successor => successor is not { } ordinal || !section.Blocks.Contains(ordinal));

        private static IEnumerable<int?> Successors(BasicBlock block)
        {
            if (block.ConditionalSuccessor is { } conditional)
                yield return conditional.Destination?.Ordinal;
            if (block.FallThroughSuccessor is { } fallThrough)
                yield return fallThrough.Destination?.Ordinal;
            if (block.ConditionalSuccessor is null && block.FallThroughSuccessor is null)
                yield return null;
        }

        /// <summary>The <c>lock</c> statement an operation opens, when the statement is one over a <c>System.Threading.Lock</c>.</summary>
        /// <param name="operation">The operation whose syntax may be a <c>lock</c> statement.</param>
        private LockSectionInfo? LockSectionOf(IOperation operation) =>
            operation.Syntax is LockStatementSyntax statement
                ? LockSections().FirstOrDefault(section => section.Statement == statement)
                : null;

        /// <summary>
        /// The <c>lock</c> sections of this body: each statement the compiler left unlowered, the expression it locks, and every
        /// block its body's operations fall in. Membership is read from the statement's own span, which is the only thing left
        /// saying where the section ends once the graph has dropped the region.
        /// </summary>
        private IReadOnlyList<LockSectionInfo> LockSections()
        {
            if (_lockSections is not null)
                return _lockSections;

            _lockSections = [];
            foreach (var block in _graph.Blocks)
            {
                foreach (var operation in block.Operations)
                {
                    if (operation.Syntax is not LockStatementSyntax statement ||
                        operation.DescendantsAndSelf().FirstOrDefault(child => child.Syntax == statement.Expression &&
                                                                               child is not IInvalidOperation) is not { } gate ||
                        Bcl.TypeName(gate.Type) != Synchronization.LOCK_TYPE)
                    {
                        continue;
                    }

                    var blocks = _graph.Blocks
                        .Where(candidate => candidate.Ordinal == block.Ordinal ||
                                            candidate.Operations.Concat(candidate.BranchValue is { } value ? [value] : [])
                                                     .Any(other => statement.Statement.Span.Contains(other.Syntax.Span)))
                        .Select(candidate => candidate.Ordinal)
                        .ToHashSet();
                    _lockSections.Add(new LockSectionInfo(statement, gate, blocks));
                }
            }

            return _lockSections;
        }

        /// <summary>One <c>lock</c> statement the compiler left unlowered: the statement, the expression it locks and the blocks
        /// its body covers.</summary>
        /// <param name="Statement">The <c>lock</c> statement.</param>
        /// <param name="Gate">The expression it locks.</param>
        /// <param name="Blocks">The blocks its body covers.</param>
        private sealed record LockSectionInfo(LockStatementSyntax Statement, IOperation Gate, HashSet<int> Blocks);

        private void LowerTop(IOperation operation)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            switch (operation)
            {
                case IExpressionStatementOperation expression:
                    LowerValue(expression.Operation);
                    break;
                case IVariableDeclarationGroupOperation group:
                    foreach (var declaration in group.Declarations)
                        LowerTop(declaration);
                    break;
                case IVariableDeclarationOperation declaration:
                    foreach (var declarator in declaration.Declarators)
                        LowerTop(declarator);
                    break;
                case IVariableDeclaratorOperation declarator when declarator.Initializer is not null:
                    StoreSymbol(declarator.Symbol,
                                declarator.Symbol is ILocalSymbol { RefKind: not RefKind.None }
                                    ? LowerAddress(declarator.Initializer.Value)
                                    : LowerValue(declarator.Initializer.Value), declarator, "declaration");
                    break;
                case IVariableDeclaratorOperation declarator:
                    GetSymbolValue(declarator.Symbol);
                    break;
                case IDeconstructionAssignmentOperation deconstruction:
                    LowerDeconstruction(deconstruction);
                    break;
                // The iterator stops here and is resumed by whoever enumerates it, possibly on another thread (R3).
                case IReturnOperation { Kind: OperationKind.YieldReturn } yielded:
                    _operations.Add(new IrYieldOperation(NextOperation(),
                                                         yielded.ReturnedValue is null ? null : LowerValue(yielded.ReturnedValue),
                                                         Provenance(yielded, "yield-return")));
                    break;
                default:
                    LowerValue(operation);
                    break;
            }
        }

        private int LowerValue(IOperation operation)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return operation switch
            {
                INameOfOperation nameOf => Constant(nameOf, "nameof"),
                IConversionOperation conversion => LowerConversion(conversion),
                IParenthesizedOperation parenthesized => LowerValue(parenthesized.Operand),
                ISimpleAssignmentOperation assignment => LowerAssignment(assignment),
                ICompoundAssignmentOperation compound => LowerCompoundAssignment(compound),
                IIncrementOrDecrementOperation increment => LowerIncrement(increment),
                IDeconstructionAssignmentOperation deconstruction => LowerDeconstruction(deconstruction),
                // A `const` is not a field but the value the compiler already put in its place, so a guard written against one is
                // a guard against that value. An enum member is such a constant and reaches the IR as its number: two members
                // of one value are one value however differently they are spelled, and a name is no proof of difference.
                IFieldReferenceOperation { Field.IsConst: true } field when field.ConstantValue.HasValue =>
                    Constant(field, "constant"),
                IFieldReferenceOperation field => LowerFieldLoad(field, "direct"),
                IEventReferenceOperation reference when FieldLikeEvents.Is(reference.Event) => LowerEventStorageLoad(reference),
                IPropertyReferenceOperation property => LowerPropertyLoad(property),
                IParameterReferenceOperation parameter => parameter.Parameter.RefKind == RefKind.None
                    ? LowerParameter(parameter) : LowerReferencedParameter(parameter),
                ILocalReferenceOperation local => local.Local.RefKind == RefKind.None
                    ? GetSymbolValue(local.Local) : LowerReferenceLoad(GetSymbolValue(local.Local), local),
                IInstanceReferenceOperation instance => instance.ReferenceKind == InstanceReferenceKind.PatternInput
                    ? _patternInputValue ?? Unknown(instance, "unsupported")
                    : _receiverValue ?? Unknown(operation, "unsupported"),
                IFlowCaptureOperation capture => LowerCapture(capture),
                IFlowCaptureReferenceOperation capture => LowerCaptureReference(capture),
                IInvocationOperation invocation => LowerInvocation(invocation),
                IObjectCreationOperation creation => LowerObjectCreation(creation),
                IArrayCreationOperation creation => LowerArrayCreation(creation),
                IAwaitOperation awaitOperation => LowerAwait(awaitOperation),
                IArrayElementReferenceOperation element => LowerElementLoad(element),
                IIsNullOperation isNull => LowerNullTest(isNull, isNull.Operand, "null-test"),
                IBinaryOperation binary when NullTestOperand(binary) is { } tested =>
                    LowerNullTest(binary, tested, "binary", Operator(binary.OperatorKind) ?? IrComparisonOperator.Equal),
                IBinaryOperation binary => LowerBinary(binary),
                IUnaryOperation unary => LowerUnary(unary),
                IIsTypeOperation typeTest => LowerTypeTest(typeTest),
                IIsPatternOperation pattern when IsNullPattern(pattern.Pattern) => LowerNullTest(pattern, pattern.Value, "is-pattern"),
                IIsPatternOperation pattern => LowerPattern(pattern.Pattern, LowerValue(pattern.Value)),
                ISwitchOperation @switch => LowerSwitch(@switch),
                ISwitchExpressionOperation @switch => LowerSwitchExpression(@switch),
                IDelegateCreationOperation delegateCreation => LowerDelegateCreation(delegateCreation),
                ICollectionExpressionOperation collection => LowerCollectionExpression(collection),
                IEventAssignmentOperation assignment => LowerEventAssignment(assignment),
                IArgumentOperation argument => LowerArgument(argument) ?? Unknown(argument, "address-taken"),
                IDynamicInvocationOperation invocation => LowerDynamicInvocation(invocation),
                IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation => LowerDynamicRead(operation),
                // A default value task is complete with its result's default, which is no object, as a default of that type would be (R1).
                IDefaultValueOperation { Type.IsValueType: true } value when Bcl.IsTask(value.Type, withValueTask: true) =>
                    Constant(value, null, "default-value-task"),
                _ when operation.ConstantValue.HasValue => Constant(operation, "constant"),
                _ => LowerUnsupported(operation)
            };
        }

        private int LowerAssignment(ISimpleAssignmentOperation assignment)
        {
            var bindsReference = UnwrapTarget(assignment.Target) is ILocalReferenceOperation { Local.RefKind: not RefKind.None } &&
                                 (assignment.Syntax is VariableDeclaratorSyntax or AssignmentExpressionSyntax { Right: RefExpressionSyntax });
            var value = bindsReference ? LowerAddress(assignment.Value) : LowerValue(assignment.Value);
            if (bindsReference && UnwrapTarget(assignment.Target) is ILocalReferenceOperation local)
                return StoreSymbol(local.Local, value, assignment, "ref-assignment");
            return LowerStore(assignment.Target, value, assignment, "assignment");
        }

        private int LowerAddress(IOperation target)
        {
            target = UnwrapTarget(target);
            switch (target)
            {
                case IFieldReferenceOperation field:
                {
                    var location = GetFieldLocation(field);
                    var address = AddTemporary(field.Type);
                    _operations.Add(new IrAddressFieldOperation(NextOperation(), address, location.Receiver, location.Field,
                                                                Provenance(field, "address")));
                    return address;
                }
                case IEventReferenceOperation reference when FieldLikeEvents.Is(reference.Event):
                {
                    var location = GetEventLocation(reference);
                    var address = AddTemporary(reference.Type);
                    _operations.Add(new IrAddressFieldOperation(NextOperation(), address, location.Receiver, location.Field,
                                                                Provenance(reference, "address")));
                    return address;
                }
                case IArrayElementReferenceOperation element:
                {
                    var receiver = LowerValue(element.ArrayReference);
                    var indices = element.Indices.Select(LowerValue).ToArray();
                    var address = AddTemporary(element.Type);
                    _operations.Add(new IrAddressElementOperation(NextOperation(), address, receiver, indices,
                                                                  Provenance(element, "address")));
                    return address;
                }
                case IPropertyReferenceOperation property when IsElementIndexer(property) && NamesOneCell(property):
                {
                    var receiver = LowerValue(property.Instance!);
                    var indices = property.Arguments.Select(argument => LowerValue(argument.Value)).ToArray();
                    var address = AddTemporary(property.Type);
                    _operations.Add(new IrAddressElementOperation(NextOperation(), address, receiver, indices,
                                                                  Provenance(property, "address")));
                    return address;
                }
                case IPropertyReferenceOperation property when IsElementIndexer(property):
                {
                    var getter = property.Property.GetMethod!;
                    var receiver = LowerValue(property.Instance!);
                    var arguments = LowerArguments(property.Arguments);
                    var address = AddCall(property, getter, receiver, arguments, property.Type);
                    _operations[^1] = AnnotateLibraryCall(AsBaseCall((IrCallOperation)_operations[^1], IsVirtualAccess(property)), getter,
                                                          property.Arguments, arguments);
                    return address;
                }
                case IInvocationOperation invocation when invocation.TargetMethod.ReturnsByRef || invocation.TargetMethod.ReturnsByRefReadonly:
                    return LowerInvocation(invocation, true);
                case ILocalReferenceOperation local when local.Local.RefKind != RefKind.None:
                    return GetSymbolValue(local.Local);
                case IParameterReferenceOperation parameter when parameter.Parameter.RefKind != RefKind.None:
                    return _parameterValues[parameter.Parameter];
                case IFlowCaptureReferenceOperation capture:
                    return LowerCaptureReference(capture);
                default:
                    return Unknown(target, "unproven-reference");
            }
        }

        private int LowerReferenceLoad(int address, IOperation source)
        {
            var result = AddTemporary(source.Type);
            _operations.Add(new IrLoadReferenceOperation(NextOperation(), result, address, Provenance(source, "reference-load")));
            return result;
        }

        private int LowerReferencedParameter(IParameterReferenceOperation parameter)
        {
            var loaded = LowerReferenceLoad(_parameterValues[parameter.Parameter], parameter);
            var current = GetSymbolValue(parameter.Parameter);
            return current == _parameterValues[parameter.Parameter] ? loaded : current;
        }

        private int LowerCompoundAssignment(ICompoundAssignmentOperation compound)
        {
            if (ReferenceTarget(compound.Target))
            {
                var address = LowerAddress(compound.Target);
                var loaded = LowerReferenceLoad(address, compound.Target);
                var loadId = ((IrLoadReferenceOperation)_operations[^1]).Id;
                var right = LowerValue(compound.Value);
                var provenance = Provenance(compound, "compound-assignment");
                var result = CompoundResult(compound, loaded, right);
                _operations.Add(new IrStoreReferenceOperation(NextOperation(), address, result, loadId, provenance));
                return result;
            }
            if (TryLocation(compound.Target, out var location))
            {
                var loaded = AddTemporary(compound.Target.Type);
                var loadId = NextOperation();
                var provenance = Provenance(compound, "compound-assignment");
                _operations.Add(new IrLoadFieldOperation(
                    loadId, loaded, location.Receiver, location.Field, provenance));
                MarkVolatile(location.Field, loadId, IrAtomicEffect.Read, provenance);
                var right = LowerValue(compound.Value);
                var result = CompoundResult(compound, loaded, right);
                var storeId = NextOperation();
                _operations.Add(new IrStoreFieldOperation(storeId, location.Receiver, location.Field, result, loadId, provenance));
                MarkVolatile(location.Field, storeId, IrAtomicEffect.Write, provenance);
                return result;
            }

            var target = UnwrapTarget(compound.Target);
            ISymbol? symbol = target switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter when !IsPrimaryConstructorParameter(parameter.Parameter) =>
                    parameter.Parameter,
                _ => null
            };
            if (symbol is not null)
            {
                var left = GetSymbolValue(symbol);
                var right = LowerValue(compound.Value);
                var result = CompoundResult(compound, left, right);
                StoreSymbol(symbol, result, compound, "compound-assignment");
                return result;
            }

            if (DynamicTarget(target) is { } dynamicTarget)
            {
                var loaded = Unknown(target, "unsupported", dynamicTarget.Operands, dynamicTarget.Get);
                var right = LowerValue(compound.Value);
                var result = CompoundResult(compound, loaded, right);
                Unknown(compound, "unsupported", [.. dynamicTarget.Operands, result], dynamicTarget.Set);
                return result;
            }

            // A target taken before a right-hand side that branches was read where it was taken; it is written through the same receiver,
            // whatever kind of target it is, as its long form writes it (R1).
            if (target is IFlowCaptureReferenceOperation captured && _capturedLongForms.TryGetValue(captured.Id, out var write))
            {
                var loaded = LowerCaptureReference(captured);
                var right = LowerValue(compound.Value);
                var result = CompoundResult(compound, loaded, right);
                write(result, compound, "compound-assignment");
                return result;
            }

            if (IsLongFormTarget(target))
            {
                return LowerLongForm(target, compound, "compound-assignment", loaded => CompoundResult(compound, loaded, LowerValue(compound.Value))).Written;
            }

            // A target the lowering does not model still evaluates its receiver, its indices and the right-hand side (R2).
            return Unknown(compound, "unsupported", [.. target.ChildOperations.Select(LowerValue), LowerValue(compound.Value)]);
        }

        /// <summary>What a compound assignment computes from the target's value and the right-hand side: the combination or removal of
        /// delegates where it is one (R3), the operator's compute otherwise.</summary>
        /// <param name="compound">The compound assignment.</param>
        /// <param name="loaded">The target's value before the assignment.</param>
        /// <param name="right">The right-hand side's value.</param>
        private int CompoundResult(ICompoundAssignmentOperation compound, int loaded, int right)
        {
            var provenance = Provenance(compound, "compound-assignment");
            if (DelegateCombination.Of(compound) is { } combination)
                return CombineDelegates(combination.Removes, [loaded, right], compound.Type, provenance);

            var result = AddTemporary(compound.Type);
            _operations.Add(new IrComputeOperation(NextOperation(), result, compound.OperatorKind.ToString(), [loaded, right], provenance));
            return result;
        }

        /// <summary>The one builder of a delegate combination or removal (R3), whatever form it is written in: a new delegate that may
        /// run every delegate its contributing operands may run.</summary>
        /// <param name="removes">Whether it removes its later operand from its first rather than combining them.</param>
        /// <param name="operands">The operand values in parameter order, the left operand or <c>source</c> first.</param>
        /// <param name="type">The type of the combined delegate.</param>
        /// <param name="provenance">The source location and lowering evidence.</param>
        private int CombineDelegates(bool removes, IReadOnlyList<int> operands, ITypeSymbol? type, IrProvenance provenance)
        {
            var result = AddTemporary(type);
            _operations.Add(new IrCombineDelegatesOperation(NextOperation(), result, removes, operands, provenance));
            return result;
        }

        /// <summary>A <c>Delegate.Combine</c>, <c>Remove</c> or <c>RemoveAll</c> as the combination it is (R3): the arguments lowered as
        /// any call's are, then the combination of the delegates they pass, of those the call lists, or of what an existing array's
        /// cells hold, read as an element load of no one cell.</summary>
        /// <param name="invocation">The call.</param>
        /// <param name="combination">What <see cref="DelegateCombination.Of"/> answered for it.</param>
        private int LowerCombinationCall(IInvocationOperation invocation, DelegateCombination combination)
        {
            var arguments = LowerArguments(invocation.Arguments);
            IReadOnlyList<int> operands;
            if (combination.CombinesCells)
            {
                var array = ArgumentValue(arguments, 0) ?? throw new UnreachableException("A combined array has no argument value.");
                var loaded = AddTemporary(((IArrayTypeSymbol)invocation.TargetMethod.Parameters[0].Type).ElementType);
                _operations.Add(new IrLoadElementOperation(NextOperation(), loaded, array, [], Provenance(invocation, "element-load"))
                {
                    NamesOneCell = false
                });
                operands = [loaded];
            }
            else if (combination.ListedIn is { } listed)
            {
                operands = ListedElements(listed) ?? throw new UnreachableException("The delegates a combination lists were not lowered.");
            }
            else
            {
                operands = combination.Operands.Select(operand => ArgumentValue(arguments, ((IArgumentOperation)operand.Parent!).Parameter!.Ordinal)
                                                                  ?? throw new UnreachableException("A combined delegate has no argument value."))
                                      .ToArray();
            }

            return CombineDelegates(combination.Removes, operands, invocation.Type, Provenance(invocation, "call"));
        }

        private int LowerIncrement(IIncrementOrDecrementOperation increment)
        {
            if (ReferenceTarget(increment.Target))
            {
                var address = LowerAddress(increment.Target);
                var refLoaded = LowerReferenceLoad(address, increment.Target);
                var refLoadId = ((IrLoadReferenceOperation)_operations[^1]).Id;
                var refOne = Constant(increment, 1, "increment");
                var refResult = AddTemporary(increment.Type);
                var refProvenance = Provenance(increment, "increment");
                _operations.Add(new IrComputeOperation(NextOperation(), refResult,
                                                      increment.Kind == OperationKind.Decrement ? "Subtract" : "Add",
                                                      [refLoaded, refOne], refProvenance));
                _operations.Add(new IrStoreReferenceOperation(NextOperation(), address, refResult, refLoadId, refProvenance));
                return increment.IsPostfix ? refLoaded : refResult;
            }
            if (!TryLocation(increment.Target, out var location))
            {
                var target = UnwrapTarget(increment.Target);
                ISymbol? symbol = target switch
                {
                    ILocalReferenceOperation local => local.Local,
                    IParameterReferenceOperation parameter when !IsPrimaryConstructorParameter(parameter.Parameter) =>
                        parameter.Parameter,
                    _ => null
                };
                if (symbol is null && DynamicTarget(target) is { } dynamicTarget)
                {
                    var dynamicLoaded = Unknown(target, "unsupported", dynamicTarget.Operands, dynamicTarget.Get);
                    var dynamicOne = Constant(increment, 1, "increment");
                    var dynamicResult = AddTemporary(increment.Type);
                    _operations.Add(new IrComputeOperation(NextOperation(), dynamicResult,
                                                          increment.Kind == OperationKind.Decrement ? "Subtract" : "Add",
                                                          [dynamicLoaded, dynamicOne], Provenance(increment, "increment")));
                    Unknown(increment, "unsupported", [.. dynamicTarget.Operands, dynamicResult], dynamicTarget.Set);
                    return increment.IsPostfix ? dynamicLoaded : dynamicResult;
                }

                if (symbol is null && IsLongFormTarget(target))
                {
                    var (read, written) = LowerLongForm(target, increment, "increment", longFormLoaded =>
                    {
                        var longFormOne = Constant(increment, 1, "increment");
                        var longFormResult = AddTemporary(increment.Type);
                        _operations.Add(new IrComputeOperation(NextOperation(), longFormResult,
                                                              increment.Kind == OperationKind.Decrement ? "Subtract" : "Add",
                                                              [longFormLoaded, longFormOne], Provenance(increment, "increment")));
                        return longFormResult;
                    });
                    return increment.IsPostfix ? read : written;
                }

                // A target the lowering does not model still evaluates its receiver and its indices (R2).
                if (symbol is null)
                    return Unknown(increment, "unsupported", target.ChildOperations.Select(LowerValue).ToArray());

                var loadedValue = GetSymbolValue(symbol);
                var oneValue = Constant(increment, 1, "increment");
                var computedValue = AddTemporary(increment.Type);
                var operation = increment.Kind == OperationKind.Decrement ? "Subtract" : "Add";
                _operations.Add(new IrComputeOperation(
                    NextOperation(), computedValue, operation, [loadedValue, oneValue],
                    Provenance(increment, "increment")));
                StoreSymbol(symbol, computedValue, increment, "increment");
                return increment.IsPostfix ? loadedValue : computedValue;
            }

            var provenance = Provenance(increment, "increment");
            var loaded = AddTemporary(increment.Target.Type);
            var loadId = NextOperation();
            _operations.Add(new IrLoadFieldOperation(loadId, loaded, location.Receiver, location.Field, provenance));
            MarkVolatile(location.Field, loadId, IrAtomicEffect.Read, provenance);
            var one = Constant(increment, 1, "increment");
            var result = AddTemporary(increment.Type);
            var @operator = increment.Kind == OperationKind.Decrement ? "Subtract" : "Add";
            _operations.Add(new IrComputeOperation(NextOperation(), result, @operator, [loaded, one], provenance));
            var storeId = NextOperation();
            _operations.Add(new IrStoreFieldOperation(storeId, location.Receiver, location.Field, result, loadId, provenance));
            MarkVolatile(location.Field, storeId, IrAtomicEffect.Write, provenance);
            return increment.IsPostfix ? loaded : result;
        }

        /// <summary>Whether a compound target is one <see cref="LowerLongForm"/> lowers: an array element of any rank, or a property the
        /// lowering calls, which has both accessors.</summary>
        /// <param name="target">The compound assignment's or increment's target.</param>
        private static bool IsLongFormTarget(IOperation target) =>
            target is IArrayElementReferenceOperation or IPropertyReferenceOperation { Property: { GetMethod: not null, SetMethod: not null } };

        /// <summary>A compound assignment or an increment as its long form <c>t = t op v</c> (R1): the receiver and every index lowered
        /// once, then the read of the target, what <paramref name="compute"/> lowers — the right-hand side and the operator — and the
        /// write of what it computed. An array element is an element load and store; a property is its getter and its setter, called
        /// with the same receiver and arguments as the long form calls them. Hands back the value read and the value written.</summary>
        /// <param name="target">The long form's target.</param>
        /// <param name="source">The operation the write's provenance names.</param>
        /// <param name="transformation">The transformation the write's provenance records.</param>
        /// <param name="compute">Lowers the right-hand side and the operator over the value read, returning the value to write.</param>
        private (int Read, int Written) LowerLongForm(IOperation target, IOperation source, string transformation, Func<int, int> compute)
        {
            var (read, write) = LowerLongFormRead(target);
            var written = compute(read);
            write(written, source, transformation);
            return (read, written);
        }

        /// <summary>The first half of a long form: the receiver and every index lowered once and the target read, with the write of a value
        /// to the same target through the same receiver and indices.</summary>
        /// <param name="target">The long form's target.</param>
        private (int Read, Action<int, IOperation, string> Write) LowerLongFormRead(IOperation target)
        {
            if (target is IPropertyReferenceOperation property)
            {
                int? receiver = property.Instance is null ? null : LowerValue(property.Instance);
                var arguments = LowerArguments(property.Arguments);
                var read = CallGetter(property, receiver, arguments);
                return (read, (written, source, transformation) => CallSetter(property, receiver, arguments, written, source, transformation));
            }

            var element = (IArrayElementReferenceOperation)target;
            var array = LowerValue(element.ArrayReference);
            var indices = element.Indices.Select(LowerValue).ToArray();
            var loaded = AddTemporary(element.Type);
            _operations.Add(new IrLoadElementOperation(NextOperation(), loaded, array, indices, Provenance(element, "element-load")));
            return (loaded, (stored, source, transformation) =>
                _operations.Add(new IrStoreElementOperation(NextOperation(), array, indices, stored, Provenance(source, transformation))));
        }

        private int LowerDeconstruction(IDeconstructionAssignmentOperation deconstruction)
        {
            var stores = new List<(IOperation Target, int Value)>();
            var model = _context.Compilation.GetSemanticModel(deconstruction.Syntax.SyntaxTree);
            var info = deconstruction.Syntax is AssignmentExpressionSyntax assignment
                ? model.GetDeconstructionInfo(assignment)
                : deconstruction.Syntax.AncestorsAndSelf().OfType<ForEachVariableStatementSyntax>().FirstOrDefault() is { } loop
                    ? model.GetDeconstructionInfo(loop)
                    : default;
            CollectDeconstruction(deconstruction.Target, deconstruction.Value, null, deconstruction, info, stores);
            foreach (var (target, value) in stores)
                LowerStore(target, value, deconstruction, "deconstruction");
            if (stores.Count == 0)
                return Unknown(deconstruction, "unsupported");
            return stores[^1].Value;
        }

        // Pairs every target leaf with its value, lowering the right-hand side left to right before any store: a tuple
        // literal element by element, and any other tuple value once, then one element per target.
        private void CollectDeconstruction(IOperation target, IOperation? source, int? sourceValue, IOperation deconstruction,
                                           DeconstructionInfo info,
                                           List<(IOperation Target, int Value)> stores)
        {
            var targetTuple = TupleOf(target);
            if (targetTuple is null)
            {
                stores.Add((target, sourceValue ?? LowerValue(source!)));
                return;
            }

            var sourceTuple = source is null ? null : TupleOf(source);
            if (sourceTuple is not null && sourceTuple.Elements.Length == targetTuple.Elements.Length)
            {
                for (var index = 0; index < targetTuple.Elements.Length; index++)
                    CollectDeconstruction(targetTuple.Elements[index], sourceTuple.Elements[index], null, deconstruction,
                                           NestedInfo(info, index), stores);
                return;
            }

            var tupleValue = sourceValue ?? LowerValue(source!);
            var outputs = info.Method is { } method && SourceDeconstruct(method)
                ? CallDeconstruct(deconstruction, method, tupleValue)
                : null;
            for (var index = 0; index < targetTuple.Elements.Length; index++)
            {
                var element = targetTuple.Elements[index];
                var elementValue = outputs is not null && index < outputs.Length
                    ? outputs[index]
                    : TupleElement(deconstruction, tupleValue, element.Type, index);
                CollectDeconstruction(element, null, elementValue, deconstruction, NestedInfo(info, index), stores);
            }
        }

        private static DeconstructionInfo NestedInfo(DeconstructionInfo info, int index) =>
            index < info.Nested.Length ? info.Nested[index] : default;

        private static bool SourceDeconstruct(IMethodSymbol method) =>
            method.DeclaringSyntaxReferences.Length != 0 && !method.IsExtern && !method.IsAbstract;

        private int TupleElement(IOperation source, int tuple, ITypeSymbol? type, int index)
        {
            var element = AddTemporary(type);
            _operations.Add(new IrComputeOperation(NextOperation(), element, $"tuple-element:Item{index + 1}", [tuple],
                                                   Provenance(source, "deconstruction")));
            return element;
        }

        private int[] CallDeconstruct(IOperation source, IMethodSymbol method, int input)
        {
            method = method.ReducedFrom ?? method;
            var extension = method.IsExtensionMethod;
            var values = new List<int>();
            var ordinals = new List<int>();
            var results = new Dictionary<int, int>();
            if (extension)
            {
                values.Add(input);
                ordinals.Add(0);
            }
            foreach (var parameter in method.Parameters.Where(parameter => parameter.RefKind == RefKind.Out))
            {
                values.Add(Constant(source, null, "out-placeholder"));
                ordinals.Add(parameter.Ordinal);
                results.Add(parameter.Ordinal, AddTemporary(parameter.Type));
            }

            _operations.Add(Call(null, method, extension ? null : input, new LoweredArguments(values, ordinals, results),
                                 Provenance(source, "deconstruction-call")));
            return results.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
        }

        private static ITupleOperation? TupleOf(IOperation operation) => operation switch
        {
            IConversionOperation conversion => TupleOf(conversion.Operand),
            IDeclarationExpressionOperation declaration => TupleOf(declaration.Expression),
            ITupleOperation tuple => tuple,
            _ => null
        };

        private int LowerStore(IOperation target, int value, IOperation source, string transformation)
        {
            target = UnwrapTarget(target);
            switch (target)
            {
                case IFieldReferenceOperation field:
                {
                    var location = GetFieldLocation(field);
                    var coalesceLoad = CoalesceLoad(target);
                    var provenance = Provenance(source, coalesceLoad is null ? transformation : "coalesce-assignment");
                    var storeId = NextOperation();
                    _operations.Add(new IrStoreFieldOperation(storeId, location.Receiver, location.Field, value, coalesceLoad, provenance));
                    MarkVolatile(location.Field, storeId, IrAtomicEffect.Write, provenance);
                    return value;
                }
                case IEventReferenceOperation reference when FieldLikeEvents.Is(reference.Event):
                {
                    var location = GetEventLocation(reference);
                    var coalesceLoad = CoalesceLoad(target);
                    _operations.Add(new IrStoreFieldOperation(
                        NextOperation(), location.Receiver, location.Field, value, coalesceLoad,
                        Provenance(source, coalesceLoad is null ? transformation : "coalesce-assignment")));
                    return value;
                }
                case IPropertyReferenceOperation property when IsAutomatic(property.Property):
                {
                    var location = GetPropertyLocation(property);
                    var coalesceLoad = CoalesceLoad(target);
                    _operations.Add(new IrStoreFieldOperation(
                        NextOperation(), location.Receiver, location.Field, value, coalesceLoad,
                        Provenance(source, coalesceLoad is null ? transformation : "coalesce-assignment")));
                    return value;
                }
                // An indexer that hands back a reference hands back one cell of its receiver's own storage, so assigning through
                // it writes that cell and never a property of the receiver (TD-043).
                case IPropertyReferenceOperation property when IsElementIndexer(property):
                {
                    if (NamesOneCell(property))
                    {
                        var receiver = LowerValue(property.Instance!);
                        var indices = property.Arguments.Select(argument => LowerValue(argument.Value)).ToArray();
                        _operations.Add(new IrStoreElementOperation(
                            NextOperation(), receiver, indices, value, Provenance(source, transformation)));
                    }
                    else
                    {
                        var address = LowerAddress(property);
                        _operations.Add(new IrStoreReferenceOperation(NextOperation(), address, value, null,
                                                                     Provenance(source, transformation)));
                    }
                    return value;
                }
                case IPropertyReferenceOperation property:
                    return LowerPropertyStore(property, value, source, transformation);
                case IInvocationOperation invocation when invocation.TargetMethod.ReturnsByRef:
                    var returnAddress = LowerAddress(invocation);
                    _operations.Add(new IrStoreReferenceOperation(NextOperation(), returnAddress, value, null,
                                                                 Provenance(source, transformation)));
                    return value;
                case IArrayElementReferenceOperation element:
                {
                    var receiver = LowerValue(element.ArrayReference);
                    var indices = element.Indices.Select(LowerValue).ToArray();
                    _operations.Add(new IrStoreElementOperation(
                        NextOperation(), receiver, indices, value, Provenance(source, transformation)));
                    return value;
                }
                case IFlowCaptureReferenceOperation capture when _capturedTargets.TryGetValue(capture.Id, out var captured):
                    return LowerStore(captured, value, source, transformation);
                case ILocalReferenceOperation local:
                    if (local.Local.RefKind != RefKind.None)
                    {
                        _operations.Add(new IrStoreReferenceOperation(NextOperation(), GetSymbolValue(local.Local), value, null,
                                                                     Provenance(source, transformation)));
                        return value;
                    }
                    return StoreSymbol(local.Local, value, source, transformation);
                case IParameterReferenceOperation parameter:
                    if (parameter.Parameter.RefKind != RefKind.None)
                    {
                        _operations.Add(new IrStoreReferenceOperation(NextOperation(), _parameterValues[parameter.Parameter], value, null,
                                                                     Provenance(source, transformation)));
                        StoreSymbol(parameter.Parameter, value, source, transformation);
                        return value;
                    }
                    if (!IsPrimaryConstructorParameter(parameter.Parameter))
                        return StoreSymbol(parameter.Parameter, value, source, transformation);
                    var primaryLocation = GetPrimaryConstructorParameterLocation(parameter.Parameter);
                    _operations.Add(new IrStoreFieldOperation(
                        NextOperation(), primaryLocation.Receiver, primaryLocation.Field, value, null,
                        Provenance(source, transformation)));
                    return value;
                // A write to a member or an indexer of a `dynamic` receiver keeps the receiver and the value it writes (R4).
                case IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation:
                    var dynamicTarget = DynamicTarget(target)!.Value;
                    Unknown(source, "unsupported", [.. dynamicTarget.Operands, value], dynamicTarget.Set);
                    return value;
                default:
                    return Unknown(source, "unsupported");
            }
        }

        private static IOperation UnwrapTarget(IOperation operation) => operation switch
        {
            IDeclarationExpressionOperation declaration => UnwrapTarget(declaration.Expression),
            IConversionOperation conversion => UnwrapTarget(conversion.Operand),
            _ => operation
        };

        private int StoreSymbol(ISymbol symbol, int sourceValue, IOperation source, string transformation)
        {
            if (!_ssaPlan.TryGetVariable(symbol, out var variable))
                throw new InvalidOperationException($"No SSA variable was planned for '{symbol.Name}'.");
            var targetValue = ResolveToken(NextDefinition(variable));
            SetCurrent(variable, targetValue);
            _operations.Add(new IrAssignOperation(
                NextOperation(), targetValue, sourceValue, Provenance(source, transformation)));
            Carry(sourceValue, targetValue);
            return targetValue;
        }

        /// <summary>
        /// What a value means to the lowering, carried to the value it is copied into. A copy is the same value, so everything
        /// the old name carried has to be found under the new one: the scope whose disposal leaves a lock, the entry an
        /// <c>await</c> of this value completes, and the task a <c>ConfigureAwait</c> result stands for. This is the one place
        /// a copy is made to mean what it copies, so a meaning added later travels without every copy site being found again
        /// (R3, ADR 0009).
        /// </summary>
        /// <param name="sourceValue">The value copied from.</param>
        /// <param name="targetValue">The value copied into.</param>
        private void Carry(int sourceValue, int targetValue)
        {
            if (_lockScopes.TryGetValue(sourceValue, out var scope))
                _lockScopes[targetValue] = scope;
            if (_pendingEntries.TryGetValue(sourceValue, out var pending))
                _pendingEntries[targetValue] = pending;
            if (_configuredTasks.TryGetValue(sourceValue, out var task))
                _configuredTasks[targetValue] = task;
        }

        /// <summary>The same for a value merged from several paths: it means what they all mean, and means nothing where they
        /// disagree — one path's scope is no proof about the value that arrives on the other.</summary>
        /// <param name="targetValue">The merged value.</param>
        /// <param name="sources">The values arriving on the merged paths.</param>
        private void Merge(int targetValue, IReadOnlyList<int> sources)
        {
            if (sources.Count == 0)
                return;

            if (Same(sources, _lockScopes, out var scope))
                _lockScopes[targetValue] = scope;
            if (Same(sources, _pendingEntries, out var pending))
                _pendingEntries[targetValue] = pending;
            if (Same(sources, _configuredTasks, out var task))
                _configuredTasks[targetValue] = task;
        }

        /// <summary>Whether every one of these values carries the same meaning, and what it is. A meaning is a value type as
        /// often as not, so "found" is answered on its own and never by testing the meaning against null.</summary>
        /// <param name="sources">The values to compare; at least one.</param>
        /// <param name="meanings">The meanings recorded per value.</param>
        /// <param name="shared">The meaning they all carry, when they agree.</param>
        private static bool Same<TMeaning>(IReadOnlyList<int> sources, Dictionary<int, TMeaning> meanings, out TMeaning shared)
        {
            shared = default!;
            if (!meanings.TryGetValue(sources[0], out var first) ||
                !sources.All(source => meanings.TryGetValue(source, out var other) && Equals(other, first)))
            {
                return false;
            }

            shared = first;
            return true;
        }

        private int LowerFieldLoad(IFieldReferenceOperation field, string transformation)
        {
            var location = GetFieldLocation(field);
            var result = AddTemporary(field.Type);
            var operationId = NextOperation();
            var provenance = Provenance(field, transformation);
            _operations.Add(new IrLoadFieldOperation(operationId, result, location.Receiver, location.Field, provenance));
            MarkVolatile(location.Field, operationId, IrAtomicEffect.Read, provenance);
            RememberCoalesceLoad(field, operationId);
            return result;
        }

        /// <summary>A field-like event read as the field it is (ADR 0014): raising it, copying it or testing it reads its storage.</summary>
        /// <param name="reference">The reference to a field-like event.</param>
        private int LowerEventStorageLoad(IEventReferenceOperation reference)
        {
            var location = GetEventLocation(reference);
            var result = AddTemporary(reference.Type);
            var operationId = NextOperation();
            _operations.Add(new IrLoadFieldOperation(operationId, result, location.Receiver, location.Field, Provenance(reference, "direct")));
            RememberCoalesceLoad(reference, operationId);
            return result;
        }

        private int LowerPropertyLoad(IPropertyReferenceOperation property)
        {
            // Reading through a ref-returning indexer reads one cell of the receiver's own storage (TD-043).
            if (IsElementIndexer(property) && !NamesOneCell(property))
                return LowerReferenceLoad(LowerAddress(property), property);
            if (IsElementIndexer(property))
            {
                var element = AddTemporary(property.Type);
                var instance = LowerValue(property.Instance!);
                var indices = property.Arguments.Select(argument => LowerValue(argument.Value)).ToArray();
                _operations.Add(new IrLoadElementOperation(
                    NextOperation(), element, instance, indices, Provenance(property, "element-load"))
                {
                    NamesOneCell = NamesOneCell(property)
                });
                return element;
            }

            if (IsAutomatic(property.Property))
            {
                var location = GetPropertyLocation(property);
                var result = AddTemporary(property.Type);
                var operationId = NextOperation();
                _operations.Add(new IrLoadFieldOperation(
                    operationId, result, location.Receiver, location.Field,
                    Provenance(property, "property-backing-field")));
                RememberCoalesceLoad(property, operationId);
                return result;
            }

            if (property.Property.GetMethod is null)
                return Unknown(property, "unsupported");
            int? receiver = property.Instance is null ? null : LowerValue(property.Instance);
            return CallGetter(property, receiver, LowerArguments(property.Arguments));
        }

        /// <summary>The call of a property's getter on a receiver and arguments already lowered, with a library model's effects bound
        /// to its indexer arguments (R4), and what a BCL getter gives, such as the task of a <c>TaskCompletionSource</c> (R1).</summary>
        /// <param name="property">The property or indexer read.</param>
        /// <param name="receiver">The receiver's value; null for a static property.</param>
        /// <param name="arguments">The indexer arguments' lowered values and ordinals.</param>
        private int CallGetter(IPropertyReferenceOperation property, int? receiver, LoweredArguments arguments)
        {
            var getter = property.Property.GetMethod!;
            var value = AddCall(property, getter, receiver, arguments, property.Type);
            _operations[^1] = AnnotateLibraryCall(AsBaseCall((IrCallOperation)_operations[^1], IsVirtualAccess(property)), getter,
                                                  property.Arguments, arguments);
            AnnotateBclCall(property, getter, (IrCallOperation)_operations[^1], property.Arguments, arguments);
            return value;
        }

        private int LowerPropertyStore(IPropertyReferenceOperation property, int value, IOperation source,
                                       string transformation)
        {
            if (property.Property.SetMethod is null)
                return Unknown(source, "unsupported");
            int? receiver = property.Instance is null ? null : LowerValue(property.Instance);
            return CallSetter(property, receiver, LowerArguments(property.Arguments), value, source, transformation);
        }

        /// <summary>The call of a property's setter with <paramref name="value"/> on a receiver and arguments already lowered, with a
        /// library model's effects bound to its indexer arguments and the value (R4).</summary>
        /// <param name="property">The property or indexer written.</param>
        /// <param name="receiver">The receiver's value; null for a static property.</param>
        /// <param name="arguments">The indexer arguments' lowered values and ordinals.</param>
        /// <param name="value">The value the setter is given.</param>
        /// <param name="source">The operation that writes the property.</param>
        /// <param name="transformation">The provenance's transformation.</param>
        private int CallSetter(IPropertyReferenceOperation property, int? receiver, LoweredArguments arguments, int value,
                               IOperation source, string transformation)
        {
            var setter = property.Property.SetMethod!;
            arguments = arguments with
            {
                Values = [.. arguments.Values, value],
                Ordinals = [.. arguments.Ordinals, setter.Parameters.Length - 1]
            };
            _operations.Add(AnnotateLibraryCall(AsBaseCall(Call(null, setter, receiver, arguments, Provenance(source, transformation)), IsVirtualAccess(property)),
                                                setter, property.Arguments, arguments));
            if (receiver is int timer && Bcl.TypeOf(setter) == Bcl.TIMERS_TIMER && property.Property.Name is "AutoReset" or "Enabled")
            {
                var action = property.Property.Name == "AutoReset" ? IrTimerAction.SetAutoReset : IrTimerAction.SetEnabled;
                _operations.Add(new IrTimerOperation(NextOperation(), action, timer, Provenance(source, "bcl"))
                {
                    Flag = Bcl.Flag((source as ISimpleAssignmentOperation)?.Value)
                });
            }

            return value;
        }

        private void RememberCoalesceLoad(IOperation operation, int operationId)
        {
            var assignment = CoalesceAssignment(operation);
            if (assignment is null)
                return;
            _coalesceLoads[CoalesceKey(assignment)] = operationId;
        }

        private int? CoalesceLoad(IOperation target)
        {
            var assignment = CoalesceAssignment(target);
            return assignment is not null && _coalesceLoads.TryGetValue(CoalesceKey(assignment), out var operationId)
                ? operationId
                : null;
        }

        private static AssignmentExpressionSyntax? CoalesceAssignment(IOperation location) =>
            location.Syntax.Ancestors()
                    .OfType<AssignmentExpressionSyntax>()
                    .FirstOrDefault(assignment => assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression)) is { } coalesce &&
            coalesce.Left.Span == location.Syntax.Span
                ? coalesce
                : null;

        private static (SyntaxTree Tree, int Start, int Length) CoalesceKey(AssignmentExpressionSyntax assignment) =>
            (assignment.SyntaxTree, assignment.SpanStart, assignment.Span.Length);

        private int LowerParameter(IParameterReferenceOperation parameter)
        {
            if (!IsPrimaryConstructorParameter(parameter.Parameter))
                return GetSymbolValue(parameter.Parameter);

            var location = GetPrimaryConstructorParameterLocation(parameter.Parameter);
            var result = AddTemporary(parameter.Type);
            _operations.Add(new IrLoadFieldOperation(
                NextOperation(), result, location.Receiver, location.Field,
                Provenance(parameter, "primary-constructor-parameter")));
            return result;
        }

        private FieldLocation GetPrimaryConstructorParameterLocation(IParameterSymbol parameter) =>
            new(_receiverValue, PrimaryConstructorParameterField(parameter));

        private bool IsPrimaryConstructorParameter(IParameterSymbol parameter)
        {
            if (SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, _method) ||
                parameter.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.Constructor })
            {
                return false;
            }

            return parameter.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax(_cancellationToken) is ParameterSyntax { Parent.Parent: TypeDeclarationSyntax });
        }

        private int LowerCapture(IFlowCaptureOperation capture)
        {
            var captured = UnwrapTarget(capture.Value);
            if (captured is IFieldReferenceOperation or IPropertyReferenceOperation ||
                captured is IParameterReferenceOperation parameter && IsPrimaryConstructorParameter(parameter.Parameter))
            {
                _capturedTargets[capture.Id] = capture.Value;
            }
            int sourceValue;
            if (capture.Syntax.Parent is RefExpressionSyntax)
            {
                sourceValue = LowerAddress(capture.Value);
            }
            else if (IsCapturedLongFormTarget(capture.Value))
            {
                // A compound assignment whose right-hand side branches takes its target here, before the branch: the receiver and the
                // indices are evaluated and the target read now, and the write after the branch goes through the same ones (R1).
                (sourceValue, var write) = LowerLongFormRead(capture.Value);
                _capturedLongForms[capture.Id] = write;
            }
            else if (DynamicTarget(capture.Value) is { } dynamicTarget)
            {
                sourceValue = Unknown(capture.Value, "unsupported", dynamicTarget.Operands, dynamicTarget.Get);
                _capturedLongForms[capture.Id] = (written, source, _) =>
                    Unknown(source, "unsupported", [.. dynamicTarget.Operands, written], dynamicTarget.Set);
            }
            else if (LowerCapturedStorageRead(capture.Value) is { } storage)
            {
                (sourceValue, var write) = storage;
                _capturedLongForms[capture.Id] = write;
            }
            else
            {
                sourceValue = LowerValue(capture.Value);
                // A local or a parameter such a compound assignment takes is written as its long form writes it (R1).
                if (UnwrapTarget(capture.Value) is ILocalReferenceOperation or IParameterReferenceOperation)
                {
                    var target = capture.Value;
                    _capturedLongForms[capture.Id] = (written, source, transformation) => LowerStore(target, written, source, transformation);
                }
            }

            var variable = _ssaPlan.GetVariable(capture.Id);
            var targetValue = ResolveToken(NextDefinition(variable));
            SetCurrent(variable, targetValue);
            _operations.Add(new IrAssignOperation(
                NextOperation(), targetValue, sourceValue, Provenance(capture, "flow-capture")));
            Carry(sourceValue, targetValue);
            return targetValue;
        }

        /// <summary>A captured target that is storage — a field, an automatic property, a primary-constructor parameter, the cell a
        /// ref-returning indexer names, or what a ref-returning call hands back — read as a load of it reads it, with the write of a value
        /// to the same storage through the receiver and indices that read evaluated, which are never evaluated again (R1). Null for every
        /// other captured operation.</summary>
        /// <param name="captured">The captured operation to read.</param>
        private (int Read, Action<int, IOperation, string> Write)? LowerCapturedStorageRead(IOperation captured)
        {
            switch (captured)
            {
                case IFieldReferenceOperation { Field.IsConst: false } or IPropertyReferenceOperation or IParameterReferenceOperation { Parameter.RefKind: RefKind.None }
                    when TryLocation(captured, out var location):
                {
                    var loaded = AddTemporary(captured.Type);
                    var loadId = NextOperation();
                    var provenance = Provenance(captured, captured switch
                    {
                        IFieldReferenceOperation => "direct",
                        IPropertyReferenceOperation => "property-backing-field",
                        _ => "primary-constructor-parameter"
                    });
                    _operations.Add(new IrLoadFieldOperation(loadId, loaded, location.Receiver, location.Field, provenance));
                    if (captured is IFieldReferenceOperation)
                        MarkVolatile(location.Field, loadId, IrAtomicEffect.Read, provenance);
                    if (captured is not IParameterReferenceOperation)
                        RememberCoalesceLoad(captured, loadId);
                    return (loaded, (written, source, transformation) =>
                    {
                        var storeId = NextOperation();
                        var storeProvenance = Provenance(source, transformation);
                        _operations.Add(new IrStoreFieldOperation(storeId, location.Receiver, location.Field, written, null, storeProvenance));
                        if (captured is IFieldReferenceOperation)
                            MarkVolatile(location.Field, storeId, IrAtomicEffect.Write, storeProvenance);
                    });
                }
                case IPropertyReferenceOperation property when IsElementIndexer(property) && NamesOneCell(property):
                {
                    var element = AddTemporary(property.Type);
                    var instance = LowerValue(property.Instance!);
                    var indices = property.Arguments.Select(argument => LowerValue(argument.Value)).ToArray();
                    _operations.Add(new IrLoadElementOperation(NextOperation(), element, instance, indices, Provenance(property, "element-load"))
                    {
                        NamesOneCell = true
                    });
                    return (element, (written, source, transformation) =>
                        _operations.Add(new IrStoreElementOperation(NextOperation(), instance, indices, written, Provenance(source, transformation))));
                }
                case IPropertyReferenceOperation property when IsElementIndexer(property):
                case IInvocationOperation { TargetMethod.ReturnsByRef: true }:
                {
                    var address = LowerAddress(captured);
                    return (LowerReferenceLoad(address, captured), (written, source, transformation) =>
                        _operations.Add(new IrStoreReferenceOperation(NextOperation(), address, written, null, Provenance(source, transformation))));
                }
                default:
                    return null;
            }
        }

        /// <summary>Whether a captured operation is a target <see cref="LowerLongForm"/> lowers, read as a load of it would read it: an array
        /// element, or a property with both accessors whose getter the lowering calls.</summary>
        /// <param name="captured">The captured operation.</param>
        private bool IsCapturedLongFormTarget(IOperation captured) =>
            captured is IArrayElementReferenceOperation ||
            captured is IPropertyReferenceOperation { Property: { GetMethod: not null, SetMethod: not null } } property &&
            !IsAutomatic(property.Property) && !IsElementIndexer(property);

        private int LowerCaptureReference(IFlowCaptureReferenceOperation capture)
        {
            return _captureValues.TryGetValue(capture.Id, out var value)
                ? value
                : Unknown(capture, "unsupported");
        }

        private int LowerInvocation(IInvocationOperation invocation, bool asAddress = false)
        {
            if (TryLowerSynchronization(invocation, out var synchronizationResult))
                return synchronizationResult;
            if (TryLowerAtomic(invocation, out var atomicResult))
                return atomicResult;

            var method = invocation.TargetMethod;
            // A partial method with no implementation is never called: the compiler removes the call together with the evaluation of
            // its arguments, so nothing runs here and there is nothing opaque about it.
            if (method.IsPartialDefinition && method.PartialImplementationPart is null)
                return Constant(invocation, null, "removed-partial-call");
            if (!asAddress && DelegateCombination.Of(invocation) is { } combination)
                return LowerCombinationCall(invocation, combination);

            int? receiver = invocation.Instance is null ? null : LowerValue(invocation.Instance);
            var arguments = LowerArguments(invocation.Arguments);
            var result = AddCall(invocation, method, receiver, arguments, invocation.Type,
                                 ServiceCalls.Of(invocation, _context.Compilation, _cancellationToken), IsAwaitedImmediately(invocation));
            var call = AsBaseCall((IrCallOperation)_operations[^1], invocation.IsVirtual);
            call = AnnotateLibraryCall(call, method, invocation.Arguments, arguments) with { CreatedArrayArguments = CreatedArrays(invocation.Arguments) };
            _operations[^1] = call;

            if (receiver is int configured && method.Name == "ConfigureAwait" && Bcl.IsTask(method.ContainingType, withValueTask: true))
                _configuredTasks[result] = configured;
            AnnotateBclCall(invocation, method, call, invocation.Arguments, arguments);
            WriteOpaqueOuts(method, invocation.Arguments, arguments);
            return asAddress || !(method.ReturnsByRef || method.ReturnsByRefReadonly)
                ? result : LowerReferenceLoad(result, invocation);
        }

        /// <summary>A method or constructor with no body at hand — none in source, or an `extern` one declared there — writes its
        /// `out` arguments where it is called, since no body will say where (R3, open question 23).</summary>
        /// <param name="method">The body-less member being called.</param>
        /// <param name="operations">The argument operations at the call site.</param>
        /// <param name="arguments">Their lowered values and output versions.</param>
        private void WriteOpaqueOuts(IMethodSymbol method, IEnumerable<IArgumentOperation> operations, LoweredArguments arguments)
        {
            if (method.DeclaringSyntaxReferences.Length != 0 && !method.IsExtern)
                return;

            var model = _context.LibraryModels.Find(method);
            bool HasOutput(IArgumentOperation argument) => model is { Kind: LibraryMatchKind.Known } && model.Outputs.ContainsKey(argument.Parameter!.Name);
            foreach (var argument in operations.Where(argument => argument.Parameter?.RefKind == RefKind.Out ||
                                                                  argument.Parameter?.RefKind == RefKind.Ref && HasOutput(argument)))
            {
                if (arguments.At(argument.Parameter!.Ordinal) is not int address)
                    continue;
                var written = HasOutput(argument) ? arguments.RefResults[argument.Parameter.Ordinal] : Unknown(argument, "opaque-out-value");
                _operations.Add(new IrStoreReferenceOperation(NextOperation(), address, written, null, Provenance(argument, "opaque-out")));
            }
        }

        /// <summary>A base call runs the base member itself, never an override of it (R1): an exact call of that member, which runs
        /// its source body where it has one and follows a library model where one describes it without a source body.</summary>
        /// <param name="call">The lowered call.</param>
        /// <param name="isVirtual">Whether the call is dispatched virtually; when not, a virtual call becomes an exact instance call.</param>
        private static IrCallOperation AsBaseCall(IrCallOperation call, bool isVirtual) =>
            !isVirtual && call.CallKind == IrCallKind.Virtual ? call with { CallKind = IrCallKind.Instance } : call;

        /// <summary>Whether a property's accessor or a method group is dispatched virtually: not through <c>base</c>, which, as a base
        /// invocation does, names the base member itself. Roslyn's <see cref="IMethodReferenceOperation.IsVirtual"/> is true for
        /// <c>base.M</c> as well, so only the receiver's syntax tells the two apart.</summary>
        /// <param name="member">The property or method reference whose receiver is tested.</param>
        private static bool IsVirtualAccess(IMemberReferenceOperation member) => member.Instance?.Syntax is not BaseExpressionSyntax;

        private int AddCall(IOperation source, IMethodSymbol method, int? receiver,
                            LoweredArguments arguments, ITypeSymbol? resultType, IrServiceCall? serviceCall = null,
                            bool awaited = false)
        {
            int? result = resultType is null || resultType.SpecialType == SpecialType.System_Void
                ? null
                : AddTemporary(resultType);
            var role = source.IsImplicit && source.Syntax.AncestorsAndSelf().OfType<CommonForEachStatementSyntax>().Any()
                ? method.Name switch
                {
                    "GetEnumerator" => IrEnumerationRole.GetEnumerator,
                    "MoveNext" => IrEnumerationRole.MoveNext,
                    "get_Current" => IrEnumerationRole.Current,
                    "Dispose" => IrEnumerationRole.Dispose,
                    _ => IrEnumerationRole.None
                }
                : IrEnumerationRole.None;
            var call = Call(result, method, receiver, arguments, Provenance(source, "call"));
            _operations.Add(call with
            {
                ServiceCall = serviceCall,
                IsAwaitedImmediately = awaited,
                EnumerationId = source.IsImplicit
                    ? source.Syntax.AncestorsAndSelf().OfType<CommonForEachStatementSyntax>().FirstOrDefault()?.SpanStart
                    : null,
                EnumerationRole = role,
                // The enumeration of a `foreach` over a library enumerable is what the recognizer of phase 4b models (ADR 0010), so its
                // members are no unresolved calls; a member of the run's own without a body is one all the same (R1).
                IsRecognized = call.IsRecognized || role != IrEnumerationRole.None && !method.Locations.Any(location => location.IsInSource)
            });
            return result ?? Constant(source, null, "void");
        }

        /// <summary>Whether the value of <paramref name="operation"/> is awaited in the same expression, directly or through a
        /// task's <c>ConfigureAwait</c>. A conditional, a <c>??</c> or a <c>switch</c> expression does not reach the await this way: the
        /// control-flow graph captures its branches first, so <see cref="MarkAwaited"/> follows those to the await instead.</summary>
        /// <param name="operation">The operation whose value is tested.</param>
        private static bool IsAwaitedImmediately(IOperation operation)
        {
            var child = operation;
            var parent = operation.Parent;
            while (parent is IConversionOperation or IParenthesizedOperation)
            {
                child = parent;
                parent = parent.Parent;
            }

            if (parent is IInvocationOperation { TargetMethod.Name: "ConfigureAwait" } configure && configure.Instance == child &&
                Bcl.IsTask(configure.TargetMethod.ContainingType, withValueTask: true))
            {
                return IsAwaitedImmediately(configure);
            }

            return parent is IAwaitOperation;
        }

        /// <summary>Follows a call of a recognized BCL member with what it starts, joins or does to a timer, or what the task it gives or
        /// completes completes with. A call of a recognized type in another form that is handed delegates becomes an unrecognized spawn
        /// of them.</summary>
        /// <param name="source">The invocation, creation or property read making the call.</param>
        /// <param name="method">The member called.</param>
        /// <param name="call">The lowered call.</param>
        /// <param name="argumentOperations">The source operations of the arguments.</param>
        /// <param name="arguments">The arguments' lowered values and ordinals.</param>
        private void AnnotateBclCall(IOperation source, IMethodSymbol method, IrCallOperation call,
                                     IEnumerable<IArgumentOperation> argumentOperations, LoweredArguments arguments)
        {
            if (IsFrameworkAwaiterMember(method))
            {
                AnnotateAwaiterCall(source, method, call);
                return;
            }

            var type = Bcl.TypeOf(method);
            if (type is null)
                return;

            var original = method.OriginalDefinition;
            var provenance = Provenance(source, "bcl");
            var delegateOrdinals = original.Parameters.Where(parameter => parameter.Type.TypeKind == TypeKind.Delegate)
                                           .Select(parameter => parameter.Ordinal)
                                           .ToArray();
            var work = delegateOrdinals.Select(ordinal => ArgumentValue(arguments, ordinal)).OfType<int>().ToList();
            var workIsAsync = delegateOrdinals.Any(ordinal => Bcl.IsAsyncDelegate(ArgumentOperation(ordinal)));
            foreach (var parameter in original.Parameters.Where(parameter => parameter.Type is IArrayTypeSymbol { ElementType.TypeKind: TypeKind.Delegate }))
            {
                if (ArgumentValue(arguments, parameter.Ordinal) is not int array)
                    continue;
                var listed = ListedElements(ArgumentOperation(parameter.Ordinal));
                work.AddRange(listed ?? [array]);
            }

            IOperation? ArgumentOperation(int ordinal) =>
                argumentOperations.FirstOrDefault(argument => argument.Parameter?.Ordinal == ordinal)?.Value;

            int? Named(string name) =>
                original.Parameters.FirstOrDefault(parameter => parameter.Name == name) is { } parameter
                    ? ArgumentValue(arguments, parameter.Ordinal)
                    : null;

            IrSpawnOperation Spawn(IrSpawnKind kind, int? handle, IReadOnlyList<int> values) =>
                new(NextOperation(), kind, call.Id, handle, values, provenance) { StateValue = Named("state"), WorkIsAsync = workIsAsync };

            IrJoinOperation Join(IrJoinKind kind) => new(NextOperation(), kind, call.Id, [call.ReceiverValue!.Value], true, provenance);

            IrTimerOperation Timer(IrTimerAction action) => new(NextOperation(), action, call.ReceiverValue!.Value, provenance);

            IrTaskOperation TaskOperation(IrTaskKind kind, IReadOnlyList<int> values) => new(NextOperation(), kind, call.Id, values, provenance);

            int[] Arguments() => Enumerable.Range(0, original.Parameters.Length).Select(ordinal => ArgumentValue(arguments, ordinal))
                                           .OfType<int>().ToArray();

            // What a task completes with (R1), whichever value carries the task: a call's result, or the value a constructor creates.
            var given = call.ResultValue ?? (method.MethodKind == MethodKind.Constructor ? call.ReceiverValue : null);
            if (TaskCompletion() is { } completion)
            {
                _operations.Add(completion);
                return;
            }

            IrTaskOperation? TaskCompletion()
            {
                switch (type, method.Name, original.Parameters.Length)
                {
                    case (Bcl.TASK or Bcl.VALUE_TASK, "FromResult", 1) when given is not null:
                        return TaskOperation(IrTaskKind.Completed, Arguments()) with { ResultValue = given };
                    // Over a task it is that task; over an IValueTaskSource it is what the source gives, which the heap does not follow;
                    // over a value, or none, it is that value or the default.
                    case (Bcl.VALUE_TASK or Bcl.VALUE_TASK_T, WellKnownMemberNames.InstanceConstructorName, 1)
                        when given is not null && Bcl.IsTask(original.Parameters[0].Type, withValueTask: false):
                        return TaskOperation(IrTaskKind.Same, []) with { ResultValue = given, TaskValue = ArgumentValue(arguments, 0) };
                    case (Bcl.VALUE_TASK or Bcl.VALUE_TASK_T, WellKnownMemberNames.InstanceConstructorName, 2) when given is not null:
                        return TaskOperation(IrTaskKind.Unfollowed, []) with { ResultValue = given };
                    case (Bcl.VALUE_TASK or Bcl.VALUE_TASK_T, WellKnownMemberNames.InstanceConstructorName, _) when given is not null:
                        return TaskOperation(IrTaskKind.Completed, Arguments()) with { ResultValue = given };
                    case (Bcl.VALUE_TASK or Bcl.VALUE_TASK_T, "AsTask", 0) when given is not null:
                    case (Bcl.TASK or Bcl.TASK_T or Bcl.VALUE_TASK or Bcl.VALUE_TASK_T, "GetAwaiter" or "ConfigureAwait", _) when given is not null:
                    case (Bcl.TCS or Bcl.TCS_T, "get_Task", 0) when given is not null:
                        return TaskOperation(IrTaskKind.Same, []) with { ResultValue = given, TaskValue = call.ReceiverValue };
                    case (Bcl.TASK or Bcl.TASK_T, "WaitAsync", _) when given is not null:
                        return TaskOperation(IrTaskKind.CompletionOf, [call.ReceiverValue!.Value]) with { ResultValue = given };
                    case (Bcl.TASK, "WhenAny", var count) when given is not null:
                        var listed = count == 1 ? ListedElements(ArgumentOperation(0)) : Arguments();
                        return TaskOperation(IrTaskKind.AnyOf, listed ?? Arguments()) with { ResultValue = given, ValuesKnown = listed is not null };
                    case (Bcl.TCS or Bcl.TCS_T, "SetResult" or "TrySetResult", _):
                        return TaskOperation(IrTaskKind.Completed, Arguments()) with { TaskValue = call.ReceiverValue };
                    case (Bcl.TCS or Bcl.TCS_T, "SetFromTask" or "TrySetFromTask", 1):
                        return TaskOperation(IrTaskKind.CompletionOf, Arguments()) with { TaskValue = call.ReceiverValue };
                    default:
                        return null;
                }
            }

            switch (type, method.Name, original.Parameters.Length)
            {
                case (Bcl.THREAD, WellKnownMemberNames.InstanceConstructorName, _):
                    _operations.Add(new IrThreadWorkOperation(NextOperation(), call.ReceiverValue!.Value, work[0], provenance)
                    {
                        WorkIsAsync = workIsAsync
                    });
                    break;
                case (Bcl.TIMER, WellKnownMemberNames.InstanceConstructorName, var count):
                    _operations.Add(Timer(IrTimerAction.Create) with
                    {
                        CallbackValue = work[0],
                        StateValue = count == 1 ? call.ReceiverValue : Named("state"),
                        DueTime = count == 1 ? IrTimerInterval.Infinite : Bcl.Interval(ArgumentOperation(2)),
                        Period = count == 1 ? IrTimerInterval.Infinite : Bcl.Interval(ArgumentOperation(3))
                    });
                    break;
                case (Bcl.TASK, "Run", _):
                    var awaitsTask = delegateOrdinals.Any(ordinal => Bcl.ReturnsTask(original.Parameters[ordinal].Type));
                    _operations.Add(Spawn(IrSpawnKind.TaskRun, call.ResultValue, work) with { AwaitsWorkTask = awaitsTask });
                    break;
                case (Bcl.TASK or Bcl.TASK_T, "ContinueWith", _):
                    _operations.Add(Spawn(IrSpawnKind.ContinueWith, call.ResultValue, work) with { AntecedentValue = call.ReceiverValue });
                    break;
                case (Bcl.FACTORY or Bcl.FACTORY_T, "StartNew", _):
                    _operations.Add(Spawn(IrSpawnKind.StartNew, call.ResultValue, work));
                    break;
                case (Bcl.THREAD_POOL, "QueueUserWorkItem", _):
                    _operations.Add(Spawn(IrSpawnKind.QueueUserWorkItem, null, work));
                    break;
                case (Bcl.THREAD_POOL, "UnsafeQueueUserWorkItem", _) when work.Count == 0 && ArgumentValue(arguments, 0) is int item:
                    _operations.Add(Spawn(IrSpawnKind.UnsafeQueueUserWorkItem, null, [item]) with
                    {
                        WorkMethod = "System.Threading.IThreadPoolWorkItem.Execute()"
                    });
                    break;
                case (Bcl.THREAD_POOL, "UnsafeQueueUserWorkItem", _):
                    _operations.Add(Spawn(IrSpawnKind.UnsafeQueueUserWorkItem, null, work));
                    break;
                case (Bcl.THREAD, "Start" or "UnsafeStart", _):
                    _operations.Add(Spawn(IrSpawnKind.ThreadStart, call.ReceiverValue, []) with { StateValue = Named("parameter") });
                    break;
                case (Bcl.PARALLEL, "For", _):
                    _operations.Add(Spawn(IrSpawnKind.ParallelFor, call.ResultValue, work) with { JoinsOnReturn = true });
                    break;
                case (Bcl.PARALLEL, "ForEach", _):
                    _operations.Add(Spawn(IrSpawnKind.ParallelForEach, call.ResultValue, work) with { JoinsOnReturn = true });
                    break;
                case (Bcl.PARALLEL, "ForEachAsync", _):
                    _operations.Add(Spawn(IrSpawnKind.ParallelForEachAsync, call.ResultValue, work) with { AwaitsWorkTask = true });
                    break;
                case (Bcl.EXTENSIONS, "Unwrap", 1) when ArgumentValue(arguments, 0) is int outer:
                    _operations.Add(new IrUnwrapOperation(NextOperation(), call.ResultValue!.Value, outer, provenance));
                    break;
                case (Bcl.TASK, "Wait", 0):
                    _operations.Add(Join(IrJoinKind.Wait));
                    break;
                // The Result of a Task<T> throws only after the task completes; that of a ValueTask<T> may throw before (R3).
                case (Bcl.TASK_T or Bcl.VALUE_TASK_T, "get_Result", 0):
                    _operations.Add(Join(IrJoinKind.Result) with { ThrowsOnlyAfterCompletion = type == Bcl.TASK_T });
                    break;
                case (Bcl.THREAD, "Join", 0):
                    _operations.Add(Join(IrJoinKind.Join));
                    break;
                case (Bcl.WAIT_HANDLE, "WaitOne", 0):
                    _operations.Add(Join(IrJoinKind.WaitOne));
                    break;
                case (Bcl.TASK, "WaitAll", 1) when original.Parameters[0].Type is IArrayTypeSymbol || Bcl.IsSpan(original.Parameters[0].Type):
                {
                    var listed = ListedElements(ArgumentOperation(0));
                    _operations.Add(new IrJoinOperation(NextOperation(), IrJoinKind.WaitAll, call.Id, listed ?? [], listed is not null,
                                                        provenance));
                    break;
                }
                case (Bcl.TASK, "WhenAll", 1):
                {
                    var listed = ListedElements(ArgumentOperation(0));
                    // Over Task<T> the group completes with a new array of every task's completion value.
                    var array = method.ReturnType is INamedTypeSymbol { TypeArguments: [var completed] } ? TypeKeyOf(completed) : null;
                    _operations.Add(new IrWhenAllOperation(NextOperation(), call.ResultValue!.Value, listed ?? [], listed is not null,
                                                           provenance)
                    {
                        ArrayTypeKey = array,
                        SourceValue = array is not null && listed is null ? ArgumentValue(arguments, 0) : null
                    });
                    break;
                }
                case (Bcl.TIMER, "Change", 2):
                    _operations.Add(Timer(IrTimerAction.Change) with
                    {
                        DueTime = Bcl.Interval(ArgumentOperation(0)),
                        Period = Bcl.Interval(ArgumentOperation(1))
                    });
                    break;
                case (Bcl.TIMER, "Dispose", 0):
                    _operations.Add(Timer(IrTimerAction.Dispose));
                    break;
                case (Bcl.TIMER, "Dispose", 1) when ArgumentValue(arguments, 0) is int waitHandle:
                    _operations.Add(Timer(IrTimerAction.DisposeWaitHandle) with { WaitHandleValue = waitHandle });
                    break;
                case (Bcl.TIMER, "DisposeAsync", 0):
                    _operations.Add(Timer(IrTimerAction.DisposeAsync) with { ResultValue = call.ResultValue });
                    break;
                case (Bcl.TIMERS_TIMER, "Start", 0):
                    _operations.Add(Timer(IrTimerAction.Start));
                    break;
                case (Bcl.TIMERS_TIMER, "Stop", 0):
                    _operations.Add(Timer(IrTimerAction.Stop));
                    break;
                default:
                    if (work.Count != 0 && Bcl.IsRecognizedType(type))
                        _operations.Add(Spawn(IrSpawnKind.Unrecognized, null, work));
                    // Any other task with a value such a type gives completes with what the heap does not follow, never with nothing,
                    // wherever it travels: a call's result, or the task a constructor creates. A task without one stays the call's
                    // result, which has no completion value to lose.
                    if (Bcl.IsTaskType(type) && call.ResultValue is int unnamed && Bcl.CarriesValue(original.ReturnType))
                        _operations.Add(TaskOperation(IrTaskKind.Unfollowed, []) with { ResultValue = unnamed });
                    else if (Bcl.IsTaskType(type) && method.MethodKind == MethodKind.Constructor && Bcl.CarriesValue(method.ContainingType) &&
                             call.ReceiverValue is int created)
                        _operations.Add(TaskOperation(IrTaskKind.Unfollowed, []) with { TaskValue = created });
                    break;
            }
        }

        /// <summary>Follows a call of a framework awaiter member the lowering recognizes: <c>GetResult()</c> of an awaiter is a
        /// <see cref="IrJoinKind.Result"/> join on the awaiter, which is the same task as the one it was taken from, and
        /// <c>GetAwaiter()</c> of a configured awaitable gives an awaiter of the same task (R1, R3).</summary>
        /// <param name="source">The invocation making the call.</param>
        /// <param name="method">The member called.</param>
        /// <param name="call">The lowered call.</param>
        private void AnnotateAwaiterCall(IOperation source, IMethodSymbol method, IrCallOperation call)
        {
            if (call.ReceiverValue is not int receiver)
                return;
            var provenance = Provenance(source, "bcl");
            if (Awaiters.IsGetResult(method, out var throwsOnlyAfterCompletion))
            {
                _operations.Add(new IrJoinOperation(NextOperation(), IrJoinKind.Result, call.Id, [receiver], true, provenance)
                {
                    ThrowsOnlyAfterCompletion = throwsOnlyAfterCompletion
                });
            }
            else if (call.ResultValue is int awaiter)
            {
                _operations.Add(new IrTaskOperation(NextOperation(), IrTaskKind.Same, call.Id, [], provenance) { ResultValue = awaiter, TaskValue = receiver });
            }
        }

        /// <summary>Binds each effect of a library call to the values it applies to (R3, R4): the call's own argument value of the
        /// effect's parameter ordinal — an invocation's or a creation's argument, an accessor's indexer argument, the value a setter
        /// is given or the handler an event accessor is given — or each element of a <c>params</c> array or slice the call creates,
        /// which are arguments of their own. A value of an immutable type touches nothing and is left out; a framework slice handed
        /// over ready is marked, since its effect is on the storage it is cut from.</summary>
        /// <param name="call">The lowered library call.</param>
        /// <param name="method">The method the call calls, whose parameters the effects name.</param>
        /// <param name="argumentOperations">The source operations of the arguments that have one; a setter's value and an event
        /// accessor's handler have none.</param>
        /// <param name="arguments">The arguments' lowered values and ordinals.</param>
        private IrCallOperation AnnotateLibraryCall(IrCallOperation call, IMethodSymbol method,
                                                    IEnumerable<IArgumentOperation> argumentOperations, LoweredArguments arguments)
        {
            if (call.Library is not { Effects.Count: > 0 } library)
                return call;

            var effects = library.Effects.Select(effect =>
            {
                if (effect.Kind == IrLibraryEffectKind.WriteCells)
                {
                    var target = effect.ParameterOrdinal == IrLibraryCall.RECEIVER ? call.ReceiverValue : ArgumentValue(arguments, effect.ParameterOrdinal);
                    return effect with { Arguments = target is int array ? [new IrLibraryArgument(array, false)] : [] };
                }
                var argument = argumentOperations.FirstOrDefault(candidate => candidate.Parameter?.Ordinal == effect.ParameterOrdinal);
                var parameter = argument?.Parameter ??
                                (effect.ParameterOrdinal >= 0 && effect.ParameterOrdinal < method.Parameters.Length ? method.Parameters[effect.ParameterOrdinal] : null);
                if (parameter is null)
                    return effect;
                if (argument is not null &&
                    argument.ArgumentKind is ArgumentKind.ParamArray or ArgumentKind.ParamCollection &&
                    ListedElements(argument.Value) is { } listed && Unwrapped(argument.Value) is var created &&
                    (created is IArrayCreationOperation { Initializer: { } initializer } ? initializer.ElementValues
                        : created is ICollectionExpressionOperation collection ? collection.Elements
                        : []) is { Length: > 0 } elements && elements.Length == listed.Count)
                {
                    return effect with
                    {
                        Arguments = listed.Zip(elements).Where(pair => !IsImmutableValue(pair.Second))
                                          .Select(pair => new IrLibraryArgument(pair.First, false)).ToArray()
                    };
                }

                // A value with no operation of its own is judged by the parameter it is handed to.
                return ArgumentValue(arguments, effect.ParameterOrdinal) is int value &&
                       !IsImmutableType(argument is null ? parameter.Type : Unwrapped(argument.Value).Type)
                    ? effect with
                    {
                        // The parameter as declared: `TEntity entity` takes one entity whatever type the call gives it.
                        Arguments = [new IrLibraryArgument(value, IsFrameworkSlice(parameter.Type))
                        {
                            IsSequence = IsSequence(parameter.OriginalDefinition.Type)
                        }]
                    }
                    : effect;
            }).ToArray();
            return call with { Library = library with { Effects = effects } };

            static IOperation Unwrapped(IOperation value) => value is IConversionOperation conversion ? Unwrapped(conversion.Operand) : value;

            static bool IsImmutableValue(IOperation value) => IsImmutableType(Unwrapped(value).Type);

            static bool IsImmutableType(ITypeSymbol? type) => type is null || LibraryModels.BuiltIn.IsImmutable(type);

            static bool IsFrameworkSlice(ITypeSymbol type) => Bcl.TypeName(type) is "System.Span`1" or "System.ReadOnlySpan`1";

            // A parameter of a sequence type takes objects and not an object: `entities` and not `entity`.
            static bool IsSequence(ITypeSymbol type) =>
                type is IArrayTypeSymbol ||
                type.SpecialType != SpecialType.System_String &&
                (type.SpecialType == SpecialType.System_Collections_IEnumerable ||
                 type.AllInterfaces.Any(@interface => @interface.SpecialType == SpecialType.System_Collections_IEnumerable));
        }

        /// <summary>The values of the tasks a call lists itself (separate arguments, an array creation or a collection expression
        /// written in the call); null for any other collection.</summary>
        /// <param name="argument">The argument operation, looked through its conversions.</param>
        private IReadOnlyList<int>? ListedElements(IOperation? argument)
        {
            while (argument is IConversionOperation conversion)
                argument = conversion.Operand;
            return argument is not null && _listedElements.TryGetValue(argument, out var elements) ? elements : null;
        }

        private static int? ArgumentValue(LoweredArguments arguments, int ordinal)
        {
            for (var index = 0; index < arguments.Ordinals.Count; index++)
            {
                if (arguments.Ordinals[index] == ordinal)
                    return arguments.Values[index];
            }

            return null;
        }

        /// <summary>A subscription to or an unsubscription from an event (R1, R2): a call of the event's add or remove accessor on its
        /// receiver with the handler, routed as any call of the accessor is — virtually, through an interface, or exactly for
        /// <c>base.E</c>. The timer's <c>Elapsed</c> is its recognizer's instead: a subscription is the timer operation, and an
        /// unsubscription does nothing.</summary>
        /// <param name="assignment">The event assignment.</param>
        private int LowerEventAssignment(IEventAssignmentOperation assignment)
        {
            if (assignment.EventReference is not IEventReferenceOperation reference)
                return LowerUnsupported(assignment);

            var @event = reference.Event;
            if (reference.Instance is not null && @event.Name == "Elapsed" && Bcl.TypeOf(@event) == Bcl.TIMERS_TIMER)
            {
                if (assignment.Adds)
                    return LowerElapsedSubscription(assignment, reference);
                LowerValue(reference.Instance);
                LowerValue(assignment.HandlerValue);
                return Constant(assignment, null, "void");
            }

            int? receiver = reference.Instance is null ? null : LowerValue(reference.Instance);
            var handler = LowerValue(assignment.HandlerValue);
            var accessor = assignment.Adds ? @event.AddMethod : @event.RemoveMethod;
            if (accessor is null)
                return Unknown(assignment, "unsupported", receiver is int value ? [value, handler] : [handler]);

            var arguments = LoweredArguments.None with { Values = [handler], Ordinals = [0] };
            var result = AddCall(assignment, accessor, receiver, arguments, null);
            _operations[^1] = AnnotateLibraryCall(AsBaseCall((IrCallOperation)_operations[^1], IsVirtualAccess(reference)), accessor, [], arguments);
            return result;
        }

        /// <summary>The timer subscription the recognizer records, after the timer and the handler are lowered.</summary>
        /// <param name="assignment">The subscription.</param>
        /// <param name="reference">The timer's <c>Elapsed</c> event.</param>
        private int LowerElapsedSubscription(IEventAssignmentOperation assignment, IEventReferenceOperation reference)
        {
            var timer = LowerValue(reference.Instance!);
            var handler = LowerValue(assignment.HandlerValue);
            _operations.Add(new IrTimerOperation(NextOperation(), IrTimerAction.ElapsedSubscribe, timer, Provenance(assignment, "bcl"))
            {
                CallbackValue = handler
            });
            return Constant(assignment, null, "void");
        }

        /// <summary>Lowered as the unsupported operation it was; its elements are remembered when none is a spread.</summary>
        /// <param name="collection">The collection expression.</param>
        private int LowerCollectionExpression(ICollectionExpressionOperation collection)
        {
            var operands = collection.ChildOperations.Select(LowerValue).ToArray();
            if (operands.Length == collection.Elements.Length && !collection.Elements.Any(element => element is ISpreadOperation))
                _listedElements[collection] = operands;
            return Unknown(collection, "unsupported", operands);
        }

        private IrCallOperation Call(int? result, IMethodSymbol method, int? receiver, LoweredArguments arguments,
                                     IrProvenance provenance)
        {
            if (method.MethodKind == MethodKind.Constructor)
                _context.RecordMetadataSupertypes(method.ContainingType);
            var nestedId = _nestedIds.GetValueOrDefault(method.OriginalDefinition);
            var collection = Collections.Of(method);
            return new IrCallOperation(
                NextOperation(), result, CallKind(method), nestedId ?? SymbolNames.Method(method), receiver, arguments.Values,
                provenance)
            {
                ArgumentParameterOrdinals = arguments.Ordinals,
                RefResults = arguments.RefResults,
                Collection = collection,
                // A call through an interface is decided by the object its receiver points to, which only the heap knows: it carries
                // what it is on each kind of object (ADR 0010, amendment of the phase 5b third run).
                Implementations = collection is null && nestedId is null ? Collections.ImplementationsOf(method, _context.Compilation, _context.LibraryModels) : [],
                Library = nestedId is null ? LibraryCalls.Of(method, _context.LibraryModels) : null,
                IsGroupingKey = nestedId is null && method.Name == "get_Key" && Bcl.TypeName(method.ContainingType) == "System.Linq.IGrouping`2",
                IsRecognized = nestedId is null && IsRecognized(method),
                TargetMethodId = nestedId is null ? RootBodyId(method.OriginalDefinition) : null,
                TargetContainingTypeKey = nestedId is null ? SymbolNames.TypeKey(method.ContainingType) : null,
                TargetMethodTypeArgumentKeys = nestedId is null ? method.TypeArguments.Select(SymbolNames.TypeKey).ToArray() : []
            };
        }

        /// <summary>Whether a recognizer of phases 1-4 models the call (R1): a member of a type one owns, or the creation of a framework
        /// slice, <c>AsSpan</c>, <c>AsMemory</c>, <c>Slice</c> or a <c>Span</c> or <c>ReadOnlySpan</c> constructor; or a member of a
        /// framework awaiter the lowering recognizes by itself (<see cref="RecognizerOf(IMethodSymbol)"/>).</summary>
        /// <param name="method">The method called.</param>
        private static bool IsRecognized(IMethodSymbol method) =>
            Bcl.TypeOf(method) is { } type &&
            (LibraryModels.IsRecognizedType(type) ||
             SpanTypes.Names(type) && (method.MethodKind == MethodKind.Constructor
                                           ? type is "System.Span`1" or "System.ReadOnlySpan`1"
                                           : method.Name is "AsSpan" or "AsMemory" or "Slice")) ||
            IsFrameworkAwaiterMember(method);

        /// <summary>Whether a call is of a member of a framework awaiter or configured awaitable that the lowering recognizes: one the
        /// owner claims, of a type declared outside source, so a same-named user type is an ordinary call.</summary>
        /// <param name="method">The method called.</param>
        private static bool IsFrameworkAwaiterMember(IMethodSymbol method) =>
            RecognizerOf(method) is not null && !method.ContainingType.OriginalDefinition.Locations.Any(location => location.IsInSource);

        /// <summary>The ordinals of the parameters whose argument is an array created in its place: a <c>params</c> array or
        /// collection, or an array creation or collection expression written as the argument (R4).</summary>
        /// <param name="arguments">The call's arguments.</param>
        private static int[] CreatedArrays(IEnumerable<IArgumentOperation> arguments) =>
            arguments.Where(argument => argument.Parameter is not null &&
                                        (argument.ArgumentKind is ArgumentKind.ParamArray or ArgumentKind.ParamCollection ||
                                         WithoutConversions(argument.Value) is IArrayCreationOperation or ICollectionExpressionOperation))
                     .Select(argument => argument.Parameter!.Ordinal)
                     .ToArray();

        private static IOperation WithoutConversions(IOperation value) =>
            value is IConversionOperation conversion ? WithoutConversions(conversion.Operand) : value;

        /// <summary>Lowers a call that enters or leaves a synchronization primitive into that entry or exit (TD-080, TD-083). An
        /// entry with a timeout holds the primitive only where its success flag is true, and an asynchronous entry only where its
        /// result is awaited; an unrecognized type stays an ordinary call and is no protection.</summary>
        /// <param name="invocation">The call to lower.</param>
        /// <param name="result">The value the lowered call produces, when it was lowered.</param>
        private bool TryLowerSynchronization(IInvocationOperation invocation, out int result)
        {
            result = 0;
            var method = invocation.TargetMethod;
            if (method.Name == "Dispose" && invocation.Instance is not null && _lockScopes.TryGetValue(LowerValue(invocation.Instance), out var scope))
            {
                _operations.Add(new IrReleaseOperation(NextOperation(), scope.LockValue, scope.Primitive, scope.Mode,
                                                       Provenance(invocation, Transformation(invocation, scope.Primitive))));
                result = Constant(invocation, null, "void");
                return true;
            }

            if (Synchronization.EffectOf(method, invocation.Instance?.Type) is not { } effect)
                return false;

            // `Monitor` names the object it works on in its first parameter; every other primitive is the object itself. Every
            // argument is lowered in the order it is written, for its side effects, and read by the parameter it is bound to.
            var isMonitor = Synchronization.IsMonitor(method);
            var arguments = LowerArguments(invocation.Arguments);
            if ((isMonitor ? arguments.At(Synchronization.MONITOR_OBJECT) : LowerValue(invocation.Instance!)) is not int lockValue)
                return false;

            var provenance = Provenance(invocation, Transformation(invocation, effect.Primitive));
            if (effect.Kind is SyncEffectKind.EnterAsync or SyncEffectKind.TryEnterAsync)
            {
                // An asynchronous wait stays the call it is and holds nothing until its result is awaited; an unawaited one holds
                // nothing at all.
                result = AddCall(invocation, method, lockValue, arguments, invocation.Type, awaited: IsAwaitedImmediately(invocation));
                _pendingEntries[result] = new PendingEntry(lockValue, effect.Primitive, effect.Mode,
                                                           effect.Kind == SyncEffectKind.TryEnterAsync);
                return true;
            }

            int? value = invocation.Type is null || invocation.Type.SpecialType == SpecialType.System_Void
                ? null
                : AddTemporary(invocation.Type);
            result = value ?? Constant(invocation, null, "void");

            switch (effect.Kind)
            {
                case SyncEffectKind.Exit:
                    _operations.Add(new IrReleaseOperation(NextOperation(), lockValue, effect.Primitive, effect.Mode, provenance)
                    {
                        Permits = Synchronization.PermitsOf(invocation, method)
                    });
                    break;
                case SyncEffectKind.Enter:
                    _operations.Add(new IrAcquireOperation(NextOperation(), lockValue, effect.Primitive, effect.Mode, provenance));
                    // `EnterScope` hands back the scope whose disposal leaves the lock.
                    if (value is int scopeValue && method.Name == "EnterScope")
                        _lockScopes[scopeValue] = new LockScope(lockValue, effect.Primitive, effect.Mode);
                    break;
                case SyncEffectKind.TryEnter:
                    // The success flag is the returned value, or the `ref bool` the call writes when it returns none — found by
                    // the parameter that declares it, since which position holds it differs between the overloads.
                    var flag = value ?? (Synchronization.SuccessFlag(method) is { } ordinal &&
                                         arguments.RefResults.TryGetValue(ordinal, out var written) ? written : 0);
                    _operations.Add(new IrAcquireOperation(NextOperation(), lockValue, effect.Primitive, effect.Mode, provenance)
                    {
                        ConditionValue = flag == 0 ? null : flag
                    });
                    break;
            }

            return true;
        }

        /// <summary>The entry an <c>await</c> completes, if the value it awaits is the result of one.</summary>
        /// <param name="awaitable">The value awaited, or a configured awaitable standing for its task.</param>
        /// <param name="awaited">The await's result, which is the success flag of a conditional entry.</param>
        /// <param name="source">The operation the entry's provenance names.</param>
        private void LowerPendingEntry(int awaitable, int? awaited, IOperation source)
        {
            var key = _configuredTasks.TryGetValue(awaitable, out var task) ? task : awaitable;
            if (!_pendingEntries.TryGetValue(key, out var entry))
                return;

            _operations.Add(new IrAcquireOperation(NextOperation(), entry.LockValue, entry.Primitive, entry.Mode,
                                                   Provenance(source, "synchronization-await"))
            {
                ConditionValue = entry.IsConditional ? awaited : null
            });
        }

        private static string Transformation(IInvocationOperation invocation, IrSynchronizationPrimitive primitive) =>
            invocation.IsImplicit && invocation.Syntax.AncestorsAndSelf().Any(syntax => syntax is LockStatementSyntax or UsingStatementSyntax)
                ? "lock-statement"
                : primitive == IrSynchronizationPrimitive.Monitor ? "monitor-call" : "synchronization-call";

        /// <summary>Lowers a call of an <c>Interlocked</c> or <c>Volatile</c> member that names one cell into the load or store it
        /// performs on that cell, marked atomic. A cell of an array is such a cell as much as a field is: nothing else in the
        /// program says that <c>Interlocked.Increment(ref slots[0])</c> changes that element at all (R1). A target the analysis
        /// does not name, a local among them, stays an ordinary call, as does every member that names no cell at all.</summary>
        /// <param name="invocation">The call to lower.</param>
        /// <param name="result">The value the lowered call produces, when it was lowered.</param>
        private bool TryLowerAtomic(IInvocationOperation invocation, out int result)
        {
            result = 0;
            // The cell is the parameter the member declares it in, whichever argument the caller wrote there.
            if (Atomics.EffectOf(invocation.TargetMethod) is not { } effect ||
                Atomics.ArgumentAt(invocation, Atomics.LOCATION) is not { } target)
            {
                return false;
            }

            if (UnwrapTarget(target.Value) is IArrayElementReferenceOperation element)
                return TryLowerAtomicElement(invocation, effect, element.ArrayReference, element.Indices, true, out result);

            // A `Span` is indexed by a ref-returning indexer, and a cell reached through one is a cell as much as a cell of an
            // array is: the atomic operation is on that cell and never on the receiver holding it (R1, TD-043).
            if (UnwrapTarget(target.Value) is IPropertyReferenceOperation indexer && IsElementIndexer(indexer))
            {
                return TryLowerAtomicElement(invocation, effect, indexer.Instance!,
                                             indexer.Arguments.Select(argument => argument.Value).ToArray(),
                                             NamesOneCell(indexer), out result);
            }

            if (!TryLocation(target.Value, out var location))
                return false;

            var provenance = Provenance(invocation, "atomic-call");
            var kind = $"{invocation.TargetMethod.ContainingType.Name}.{invocation.TargetMethod.Name}";
            var arguments = LowerArguments(invocation.Arguments.Where(argument => argument.Parameter?.Ordinal != Atomics.LOCATION));
            if (effect == IrAtomicEffect.Read)
            {
                var loaded = AddTemporary(invocation.Type);
                var loadId = NextOperation();
                _operations.Add(new IrLoadFieldOperation(loadId, loaded, location.Receiver, location.Field, provenance));
                _operations.Add(Atomic(loadId, effect, kind, null, provenance));
                result = loaded;
                return true;
            }

            // The stored value is the one the call is given; a member that computes it, an increment among them, names a number
            // no value of this body holds.
            var stored = arguments.At(Atomics.VALUE) ?? Constant(invocation, null, "atomic-call");
            var storeId = NextOperation();
            _operations.Add(new IrStoreFieldOperation(storeId, location.Receiver, location.Field, stored, null, provenance));
            int? replaced = invocation.Type is null || invocation.Type.SpecialType == SpecialType.System_Void
                ? null
                : AddTemporary(invocation.Type);
            _operations.Add(Atomic(storeId, effect, kind, replaced, provenance, ComparandOf(effect, arguments)));
            result = replaced ?? Constant(invocation, null, "void");
            return true;
        }

        /// <summary>The same as <see cref="TryLowerAtomic"/> for a cell of a collection, which is a cell of the collection's own
        /// resource and not of any field of it (ADR 0010).</summary>
        /// <param name="invocation">The call to lower.</param>
        /// <param name="effect">The atomic effect of the called member.</param>
        /// <param name="collection">The collection whose cell the call names.</param>
        /// <param name="indexOperations">The indices of the cell.</param>
        /// <param name="namesOneCell">Whether the indices name exactly one cell of the collection.</param>
        /// <param name="result">The value the lowered call produces.</param>
        private bool TryLowerAtomicElement(IInvocationOperation invocation, IrAtomicEffect effect, IOperation collection,
                                           IReadOnlyList<IOperation> indexOperations, bool namesOneCell, out int result)
        {
            var provenance = Provenance(invocation, "atomic-call");
            var kind = $"{invocation.TargetMethod.ContainingType.Name}.{invocation.TargetMethod.Name}";
            var receiver = LowerValue(collection);
            var indices = indexOperations.Select(LowerValue).ToArray();
            var arguments = LowerArguments(invocation.Arguments.Where(argument => argument.Parameter?.Ordinal != Atomics.LOCATION));
            if (effect == IrAtomicEffect.Read)
            {
                var loaded = AddTemporary(invocation.Type);
                var loadId = NextOperation();
                _operations.Add(new IrLoadElementOperation(loadId, loaded, receiver, indices, provenance) { NamesOneCell = namesOneCell });
                _operations.Add(Atomic(loadId, effect, kind, null, provenance));
                result = loaded;
                return true;
            }

            var stored = arguments.At(Atomics.VALUE) ?? Constant(invocation, null, "atomic-call");
            var storeId = NextOperation();
            _operations.Add(new IrStoreElementOperation(storeId, receiver, indices, stored, provenance) { NamesOneCell = namesOneCell });
            int? replaced = invocation.Type is null || invocation.Type.SpecialType == SpecialType.System_Void
                ? null
                : AddTemporary(invocation.Type);
            _operations.Add(Atomic(storeId, effect, kind, replaced, provenance, ComparandOf(effect, arguments)));
            result = replaced ?? Constant(invocation, null, "void");
            return true;
        }

        /// <summary>Marks a field load or store atomic when its field is <c>volatile</c>: each read and write of such a field is
        /// atomic on its cell, although a read-modify-write built from two of them is not (TD-082).</summary>
        /// <param name="field">The field loaded or stored.</param>
        /// <param name="operationId">The id of the load or store operation.</param>
        /// <param name="effect">Whether the access is a read or a write.</param>
        /// <param name="provenance">The provenance of the access.</param>
        private void MarkVolatile(IrFieldRef field, int operationId, IrAtomicEffect effect, IrProvenance provenance)
        {
            if (field.IsVolatile)
                _operations.Add(Atomic(operationId, effect, Atomics.VOLATILE_FIELD, null, provenance));
        }

        private IrAtomicOperation Atomic(int targetOperationId, IrAtomicEffect effect, string kind, int? result, IrProvenance provenance,
                                         int? comparand = null) =>
            new(NextOperation(), result, kind, [], provenance)
            {
                Effect = effect,
                TargetOperationId = targetOperationId,
                ComparandValue = comparand
            };

        /// <summary>The value a compare-and-swap checks the cell against, which is the parameter it declares for it; every other
        /// member checks nothing and writes what it was handed (R1).</summary>
        /// <param name="effect">The atomic effect of the called member.</param>
        /// <param name="arguments">The call's lowered arguments.</param>
        private static int? ComparandOf(IrAtomicEffect effect, LoweredArguments arguments) =>
            effect == IrAtomicEffect.CompareAndSwap ? arguments.At(Atomics.COMPARAND) : null;

        /// <summary>Lowers the arguments in evaluation order, then defines a new version of every local or parameter passed by
        /// <c>ref</c> or <c>out</c>, keyed by the parameter it binds.</summary>
        /// <param name="arguments">The arguments to lower in evaluation order.</param>
        private LoweredArguments LowerArguments(IEnumerable<IArgumentOperation> arguments)
        {
            var values = new List<int>();
            var ordinals = new List<int>();
            var passedByReference = new List<(int Ordinal, ISymbol Symbol)>();
            var refResults = new Dictionary<int, int>();
            foreach (var argument in arguments)
            {
                var ordinal = argument.Parameter?.Ordinal ?? -1;
                if (LowerArgument(argument) is int value)
                {
                    values.Add(value);
                    ordinals.Add(ordinal);
                }

                var output = argument.Parameter is { RefKind: RefKind.Ref or RefKind.Out } parameter &&
                             _context.LibraryModels.Find((IMethodSymbol)parameter.ContainingSymbol) is { Kind: LibraryMatchKind.Known } model &&
                             model.Outputs.ContainsKey(parameter.Name);
                if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out && RefArgumentSymbol(argument) is { } symbol &&
                    !(output && symbol is ILocalSymbol { RefKind: not RefKind.None }))
                    passedByReference.Add((ordinal, symbol));
                else if (output)
                    refResults[ordinal] = AddTemporary(argument.Parameter!.Type);
            }

            var finalVersions = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
            foreach (var (ordinal, symbol) in passedByReference)
            {
                if (!_ssaPlan.TryGetVariable(symbol, out var variable))
                    throw new InvalidOperationException($"No SSA variable was planned for '{symbol.Name}'.");
                var defined = ResolveToken(NextDefinition(variable));
                SetCurrent(variable, defined);
                finalVersions[symbol] = defined;
            }
            foreach (var (ordinal, symbol) in passedByReference)
                refResults[ordinal] = finalVersions[symbol];

            return new LoweredArguments(values, ordinals, refResults);
        }

        private ISymbol? RefArgumentSymbol(IArgumentOperation argument) => UnwrapTarget(argument.Value) switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation parameter when !IsPrimaryConstructorParameter(parameter.Parameter) => parameter.Parameter,
            _ => null
        };

        private int? LowerArgument(IArgumentOperation argument)
        {
            if (argument.Parameter?.RefKind is not (RefKind.Ref or RefKind.Out or RefKind.In or RefKind.RefReadOnlyParameter))
                return LowerValue(argument.Value);
            var target = UnwrapTarget(argument.Value);
            if (target is ILocalReferenceOperation { Local.RefKind: RefKind.None } local)
            {
                var value = GetSymbolValue(local.Local);
                _operations.Add(new IrUnknownOperation(NextOperation(), null, argument.Value.Kind.ToString(), "address-taken",
                                                       [value], Provenance(argument, "address-taken")));
                return value;
            }
            if (target is IParameterReferenceOperation { Parameter.RefKind: RefKind.None } parameter &&
                !IsPrimaryConstructorParameter(parameter.Parameter))
            {
                var value = GetSymbolValue(parameter.Parameter);
                _operations.Add(new IrUnknownOperation(NextOperation(), null, argument.Value.Kind.ToString(), "address-taken",
                                                       [value], Provenance(argument, "address-taken")));
                return value;
            }
            return LowerAddress(argument.Value);
        }

        private static bool ReferenceTarget(IOperation target) => UnwrapTarget(target) switch
        {
            ILocalReferenceOperation local => local.Local.RefKind != RefKind.None,
            IParameterReferenceOperation parameter => parameter.Parameter.RefKind != RefKind.None,
            IPropertyReferenceOperation property => IsElementIndexer(property) && !NamesOneCell(property),
            IInvocationOperation invocation => invocation.TargetMethod.ReturnsByRef,
            _ => false
        };

        private int LowerObjectCreation(IObjectCreationOperation creation)
        {
            _context.RecordMetadataSupertypes(creation.Type);
            var result = AddTemporary(creation.Type);
            var provenance = Provenance(creation, "object-creation");
            _operations.Add(new IrAllocateOperation(NextOperation(), result, TypeName(creation.Type), provenance)
            {
                AllocatedTypeKey = TypeKeyOf(creation.Type),
                SiteOrdinal = _context.SiteOrdinals.Of(creation),
                SynchronizationCapacity = Synchronization.CapacityOf(creation),
                KeyEquality = Collections.EqualityOf(creation)
            });
            if (creation.Constructor is not null)
            {
                var arguments = LowerArguments(creation.Arguments);
                _operations.Add(Call(null, creation.Constructor, result, arguments, provenance));
                _operations[^1] = AnnotateLibraryCall((IrCallOperation)_operations[^1], creation.Constructor, creation.Arguments, arguments) with
                {
                    CreatedArrayArguments = CreatedArrays(creation.Arguments)
                };
                AnnotateBclCall(creation, creation.Constructor, (IrCallOperation)_operations[^1], creation.Arguments, arguments);
                WriteOpaqueOuts(creation.Constructor, creation.Arguments, arguments);
            }

            return result;
        }

        /// <summary>An array creation evaluates its sizes, allocates the array and stores each initializer element in order.</summary>
        /// <param name="creation">The array creation.</param>
        private int LowerArrayCreation(IArrayCreationOperation creation)
        {
            _context.RecordMetadataSupertypes(creation.Type);
            foreach (var size in creation.DimensionSizes)
                LowerValue(size);
            var result = AddTemporary(creation.Type);
            _operations.Add(new IrAllocateOperation(NextOperation(), result, TypeName(creation.Type), Provenance(creation, "array-creation"))
            {
                AllocatedTypeKey = TypeKeyOf(creation.Type),
                SiteOrdinal = _context.SiteOrdinals.Of(creation)
            });
            if (creation.Initializer is not null)
            {
                var elements = new List<int>();
                StoreElements(result, creation.Initializer, [], creation.DimensionSizes[0].Type, elements);
                if (creation.DimensionSizes.Length == 1)
                    _listedElements[creation] = elements;
            }

            return result;
        }

        private void StoreElements(int array, IArrayInitializerOperation initializer, IReadOnlyList<int> outerIndices,
                                   ITypeSymbol? indexType, List<int> elements)
        {
            for (var position = 0; position < initializer.ElementValues.Length; position++)
            {
                var element = initializer.ElementValues[position];
                var index = AddValue(IrValueKind.Constant, indexType, position.ToString(CultureInfo.InvariantCulture), 0);
                if (element is IArrayInitializerOperation nested)
                {
                    StoreElements(array, nested, [.. outerIndices, index], indexType, elements);
                    continue;
                }

                var value = LowerValue(element);
                elements.Add(value);
                _operations.Add(new IrStoreElementOperation(
                    NextOperation(), array, [.. outerIndices, index], value, Provenance(element, "array-initializer")));
            }
        }

        private int LowerAwait(IAwaitOperation awaitOperation)
        {
            var awaitable = LowerValue(awaitOperation.Operation);
            int? result = awaitOperation.Type is null || awaitOperation.Type.SpecialType == SpecialType.System_Void
                ? null
                : AddTemporary(awaitOperation.Type);
            _operations.Add(new IrAwaitOperation(
                NextOperation(), result, awaitable, Provenance(awaitOperation, "await"))
            {
                TaskValue = _configuredTasks.TryGetValue(awaitable, out var task) ? task : null
            });
            LowerPendingEntry(awaitable, result, awaitOperation);
            return result ?? Constant(awaitOperation, null, "void");
        }

        private int LowerElementLoad(IArrayElementReferenceOperation element)
        {
            var receiver = LowerValue(element.ArrayReference);
            var indices = element.Indices.Select(LowerValue).ToArray();
            var result = AddTemporary(element.Type);
            _operations.Add(new IrLoadElementOperation(
                NextOperation(), result, receiver, indices, Provenance(element, "element-load")));
            return result;
        }

        private int LowerBinary(IBinaryOperation binary)
        {
            var left = LowerValue(binary.LeftOperand);
            var right = LowerValue(binary.RightOperand);
            if (DelegateCombination.Of(binary) is { } combination)
                return CombineDelegates(combination.Removes, [left, right], binary.Type, Provenance(binary, "binary"));

            var result = AddTemporary(binary.Type);
            if (binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
            {
                _operations.Add(new IrCompareOperation(
                    NextOperation(), result, IrComparisonKind.Equality, left, right, null,
                    Provenance(binary, "binary"))
                {
                    Operator = Operator(binary.OperatorKind)
                });
            }
            else if (binary.OperatorKind is BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or
                     BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual)
            {
                _operations.Add(new IrCompareOperation(
                    NextOperation(), result, IrComparisonKind.Ordering, left, right, null,
                    Provenance(binary, "binary"))
                {
                    Operator = Operator(binary.OperatorKind)
                });
            }
            else
            {
                _operations.Add(new IrComputeOperation(
                    NextOperation(), result, binary.OperatorKind.ToString(), [left, right],
                    Provenance(binary, "binary")));
            }
            return result;
        }

        /// <summary>A null test of <paramref name="tested"/>. Which way it runs is the comparison's operator: the compare itself
        /// is the same for <c>== null</c> and <c>!= null</c>.</summary>
        /// <param name="test">The testing operation, whose type the result has and which the provenance names.</param>
        /// <param name="tested">The operation whose value is tested against null.</param>
        /// <param name="transformation">The transformation the provenance records.</param>
        /// <param name="comparison">The comparison's operator: equal for <c>== null</c>, not equal for <c>!= null</c>.</param>
        private int LowerNullTest(IOperation test, IOperation tested, string transformation,
                                  IrComparisonOperator comparison = IrComparisonOperator.Equal)
        {
            var value = LowerValue(tested);
            var result = AddTemporary(test.Type);
            _operations.Add(new IrCompareOperation(
                NextOperation(), result, IrComparisonKind.Null, value, null, null, Provenance(test, transformation))
            {
                Operator = comparison
            });
            return result;
        }

        /// <summary>The comparison an operator kind names.</summary>
        /// <param name="kind">The binary operator kind.</param>
        private static IrComparisonOperator? Operator(BinaryOperatorKind kind) => kind switch
        {
            BinaryOperatorKind.Equals => IrComparisonOperator.Equal,
            BinaryOperatorKind.NotEquals => IrComparisonOperator.NotEqual,
            BinaryOperatorKind.LessThan => IrComparisonOperator.Less,
            BinaryOperatorKind.LessThanOrEqual => IrComparisonOperator.LessOrEqual,
            BinaryOperatorKind.GreaterThan => IrComparisonOperator.Greater,
            BinaryOperatorKind.GreaterThanOrEqual => IrComparisonOperator.GreaterOrEqual,
            _ => null
        };

        /// <summary>The operand <c>==</c> or <c>!=</c> compares with the <c>null</c> constant, when the operand is a reference
        /// or nullable value type and no user-defined operator is involved.</summary>
        /// <param name="binary">The binary operation to inspect.</param>
        private static IOperation? NullTestOperand(IBinaryOperation binary)
        {
            if (binary.OperatorKind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals) || binary.OperatorMethod is not null)
                return null;
            var tested = IsNullConstant(binary.RightOperand) ? binary.LeftOperand
                : IsNullConstant(binary.LeftOperand) ? binary.RightOperand
                : null;
            return tested is { Type: { IsReferenceType: true } or { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } } &&
                   !IsNullConstant(tested)
                ? tested
                : null;
        }

        private static bool IsNullConstant(IOperation operation) => operation.ConstantValue is { HasValue: true, Value: null };

        private static bool IsNullPattern(IPatternOperation pattern) => pattern switch
        {
            IConstantPatternOperation constant => IsNullConstant(constant.Value),
            INegatedPatternOperation { Pattern: IConstantPatternOperation constant } => IsNullConstant(constant.Value),
            _ => false
        };

        private int LowerUnary(IUnaryOperation unary)
        {
            var operand = LowerValue(unary.Operand);
            var result = AddTemporary(unary.Type);
            _operations.Add(new IrComputeOperation(
                NextOperation(), result, unary.OperatorKind.ToString(), [operand], Provenance(unary, "unary")));
            return result;
        }

        private int LowerTypeTest(IIsTypeOperation typeTest)
        {
            var value = LowerValue(typeTest.ValueOperand);
            var result = AddTemporary(typeTest.Type);
            _operations.Add(new IrCompareOperation(
                NextOperation(), result, IrComparisonKind.Type, value, null,
                SymbolNames.Type(typeTest.TypeOperand), Provenance(typeTest, "type-test"))
            {
                Operator = typeTest.IsNegated ? IrComparisonOperator.NotEqual : IrComparisonOperator.Equal
            });
            return result;
        }

        private int LowerPattern(IPatternOperation pattern, int input)
        {
            switch (pattern)
            {
                case IDeclarationPatternOperation declaration:
                    if (declaration.DeclaredSymbol is { } symbol)
                        StoreSymbol(symbol, input, declaration, "pattern-designation");
                    return PatternTest(pattern, input);
                case IRecursivePatternOperation recursive:
                    if (recursive.DeconstructionSubpatterns.Length != 0 && recursive.DeconstructSymbol is not IMethodSymbol &&
                        recursive.InputType is not INamedTypeSymbol { IsTupleType: true })
                        return LowerUnsupported(recursive);
                    if (recursive.DeclaredSymbol is { } declared)
                        StoreSymbol(declared, input, recursive, "pattern-designation");
                    var positional = recursive.DeconstructionSubpatterns.Length == 0 ? [] :
                        recursive.DeconstructSymbol is IMethodSymbol method
                            ? CallDeconstruct(recursive, method, input)
                            : recursive.DeconstructionSubpatterns.Select((part, index) =>
                                TupleElement(recursive, input, part.InputType, index)).ToArray();
                    var positionalTests = recursive.DeconstructionSubpatterns.Select((part, index) => LowerPattern(part, positional[index])).ToArray();
                    var nested = recursive.PropertySubpatterns.Select(property => LowerPropertySubpattern(property, input)).ToArray();
                    return PatternTest(pattern, [input, .. positionalTests, .. nested]);
                case IListPatternOperation list:
                    return LowerListPattern(list, input);
                case ISlicePatternOperation slice:
                    return LowerSlicePattern(slice, input, 0, input);
                case ITypePatternOperation type:
                    var result = AddTemporary(type.Type);
                    _operations.Add(new IrCompareOperation(NextOperation(), result, IrComparisonKind.Type, input, null,
                                                           SymbolNames.Type(type.MatchedType), Provenance(type, "type-test")));
                    return result;
                case IConstantPatternOperation constant:
                    return PatternTest(pattern, input, LowerValue(constant.Value));
                case IRelationalPatternOperation relational:
                    return PatternTest(pattern, input, LowerValue(relational.Value));
                case IBinaryPatternOperation binary:
                    return PatternTest(pattern, LowerPattern(binary.LeftPattern, input), LowerPattern(binary.RightPattern, input));
                case INegatedPatternOperation negated:
                    return PatternTest(pattern, LowerPattern(negated.Pattern, input));
                case IDiscardPatternOperation:
                    return PatternTest(pattern, input);
                default:
                    return LowerUnsupported(pattern);
            }
        }

        private int LowerPropertySubpattern(IPropertySubpatternOperation property, int input)
        {
            var previous = _patternInputValue;
            _patternInputValue = input;
            var member = LowerValue(property.Member);
            _patternInputValue = previous;
            return LowerPattern(property.Pattern, member);
        }

        private int LowerListPattern(IListPatternOperation list, int input)
        {
            if (list.DeclaredSymbol is { } symbol)
                StoreSymbol(symbol, input, list, "pattern-designation");
            var length = PatternLength(list, input);
            var tests = new List<int> { input, length };
            var sliceIndex = Array.FindIndex(list.Patterns.ToArray(), pattern => pattern is ISlicePatternOperation);
            for (var index = 0; index < list.Patterns.Length; index++)
            {
                var pattern = list.Patterns[index];
                tests.Add(pattern is ISlicePatternOperation slice
                    ? LowerSlicePattern(slice, input, index, PatternSubtract(list, length, list.Patterns.Length - 1))
                    : LowerPattern(pattern, PatternElement(list, input,
                        sliceIndex >= 0 && index > sliceIndex ? PatternSubtract(list, length, list.Patterns.Length - index)
                            : Constant(list, index, "pattern-index"))));
            }
            return PatternTest(list, [.. tests]);
        }

        private int PatternLength(IListPatternOperation list, int input)
        {
            if (list.InputType is IArrayTypeSymbol || list.InputType?.SpecialType == SpecialType.System_String)
            {
                var length = AddTemporary(_context.Compilation.GetSpecialType(SpecialType.System_Int32));
                _operations.Add(new IrComputeOperation(NextOperation(), length, "pattern-length", [input], Provenance(list, "pattern-length")));
                return length;
            }
            return list.LengthSymbol is IPropertySymbol { GetMethod: { } getter } property
                ? AddCall(list, getter, input, LoweredArguments.None, property.Type)
                : Unknown(list, "pattern-test", [input]);
        }

        private int PatternSubtract(IOperation source, int length, int amount)
        {
            var result = AddTemporary(_context.Compilation.GetSpecialType(SpecialType.System_Int32));
            _operations.Add(new IrComputeOperation(NextOperation(), result, "Subtract", [length, Constant(source, amount, "pattern-count")],
                                                   Provenance(source, "pattern-index")));
            return result;
        }

        private int PatternElement(IListPatternOperation list, int input, int index)
        {
            if (list.InputType is IArrayTypeSymbol array)
            {
                var result = AddTemporary(array.ElementType);
                _operations.Add(new IrLoadElementOperation(NextOperation(), result, input, [index], Provenance(list, "pattern-element")));
                return result;
            }
            if (list.InputType?.SpecialType == SpecialType.System_String)
            {
                var result = AddTemporary(_context.Compilation.GetSpecialType(SpecialType.System_Char));
                _operations.Add(new IrComputeOperation(NextOperation(), result, "pattern-character", [input, index], Provenance(list, "pattern-element")));
                return result;
            }
            return list.IndexerSymbol is IPropertySymbol { GetMethod: { } getter } property
                ? AddCall(list, getter, input, new LoweredArguments([index], [0], new Dictionary<int, int>()), property.Type)
                : Unknown(list, "pattern-test", [input]);
        }

        private int LowerSlicePattern(ISlicePatternOperation slice, int input, int position, int length)
        {
            if (slice.Pattern is null)
                return PatternTest(slice, input);
            var start = Constant(slice, position, "slice-start");
            var rest = slice.SliceSymbol switch
            {
                IMethodSymbol method => AddCall(slice, method, input,
                    new LoweredArguments([start, length], [0, 1], new Dictionary<int, int>()), method.ReturnType),
                IPropertySymbol { GetMethod: { } getter } property => AddCall(slice, getter, input,
                    new LoweredArguments([PatternRange(slice, start, length)], [0], new Dictionary<int, int>()), property.Type),
                _ => input
            };
            return LowerPattern(slice.Pattern, rest);
        }

        private int PatternRange(ISlicePatternOperation slice, int start, int length)
        {
            var indexType = _context.Compilation.GetTypeByMetadataName("System.Index")!;
            var rangeType = _context.Compilation.GetTypeByMetadataName("System.Range")!;
            var indexConstructor = indexType.InstanceConstructors.Single(method => method.Parameters.Length == 2);
            var rangeConstructor = rangeType.InstanceConstructors.Single(method => method.Parameters.Length == 2);
            var end = AddTemporary(_context.Compilation.GetSpecialType(SpecialType.System_Int32));
            _operations.Add(new IrComputeOperation(NextOperation(), end, "Add", [start, length], Provenance(slice, "slice-end")));

            int Index(int position)
            {
                var result = AddTemporary(indexType);
                _operations.Add(new IrAllocateOperation(NextOperation(), result, TypeName(indexType), Provenance(slice, "pattern-index"))
                {
                    AllocatedTypeKey = TypeKeyOf(indexType)
                });
                _operations.Add(Call(null, indexConstructor, result,
                    new LoweredArguments([position, Constant(slice, false, "from-start")], [0, 1], new Dictionary<int, int>()),
                    Provenance(slice, "pattern-index")));
                return result;
            }

            var range = AddTemporary(rangeType);
            _operations.Add(new IrAllocateOperation(NextOperation(), range, TypeName(rangeType), Provenance(slice, "pattern-range"))
            {
                AllocatedTypeKey = TypeKeyOf(rangeType)
            });
            _operations.Add(Call(null, rangeConstructor, range,
                new LoweredArguments([Index(start), Index(end)], [0, 1], new Dictionary<int, int>()),
                Provenance(slice, "pattern-range")));
            return range;
        }

        private int PatternTest(IPatternOperation pattern, params int[] operands) => Unknown(pattern, "pattern-test", operands);

        private int LowerSwitch(ISwitchOperation @switch)
        {
            var input = LowerValue(@switch.Value);
            foreach (var @case in @switch.Cases)
            {
                foreach (var clause in @case.Clauses.OfType<IPatternCaseClauseOperation>())
                {
                    LowerPattern(clause.Pattern, input);
                    if (clause.Guard is not null)
                        LowerValue(clause.Guard);
                }
                foreach (var body in @case.Body)
                    LowerTop(body);
            }
            return Unknown(@switch, "pattern-test", [input]);
        }

        private int LowerSwitchExpression(ISwitchExpressionOperation @switch)
        {
            var input = LowerValue(@switch.Value);
            foreach (var arm in @switch.Arms)
            {
                LowerPattern(arm.Pattern, input);
                if (arm.Guard is not null)
                    LowerValue(arm.Guard);
                LowerValue(arm.Value);
            }
            return Unknown(@switch, "pattern-test", [input]);
        }

        private int LowerConversion(IConversionOperation conversion)
        {
            var operand = LowerValue(conversion.Operand);
            var result = AddTemporary(conversion.Type);
            var classified = Microsoft.CodeAnalysis.CSharp.CSharpExtensions.GetConversion(conversion);
            var kind = classified.IsIdentity ? IrConversionKind.Identity
                : classified.IsReference ? IrConversionKind.Reference
                : classified.IsBoxing ? IrConversionKind.Boxing
                : classified.IsUnboxing ? IrConversionKind.Unboxing
                : classified.IsNumeric ? IrConversionKind.Numeric
                : classified.IsUserDefined ? IrConversionKind.UserDefined
                : IrConversionKind.Other;
            _operations.Add(new IrConvertOperation(
                NextOperation(), result, operand, TypeName(conversion.Type), kind,
                Provenance(conversion, "conversion")));
            // A conversion that keeps the value keeps what it means; one that makes a number of it means nothing here.
            if (kind is not IrConversionKind.Numeric)
                Carry(operand, result);
            return result;
        }

        private int LowerDelegateCreation(IDelegateCreationOperation delegateCreation)
        {
            var result = AddTemporary(delegateCreation.Type);
            string? targetBodyId = null;
            string? targetMethod = null;
            IMethodSymbol? target = null;
            IReadOnlyList<string> captured = [];
            int? receiver = null;
            var isNonVirtual = false;
            switch (delegateCreation.Target)
            {
                case IFlowAnonymousFunctionOperation anonymousFunction:
                    targetBodyId = _nestedIds.GetValueOrDefault(anonymousFunction.Symbol);
                    captured = CapturedSymbolKeys(anonymousFunction.Symbol,
                                                  _graph.GetAnonymousFunctionControlFlowGraph(anonymousFunction, _cancellationToken));
                    break;
                case IMethodReferenceOperation methodReference:
                    var method = methodReference.Method.OriginalDefinition;
                    if (_nestedIds.TryGetValue(method, out var localBodyId))
                    {
                        targetBodyId = localBodyId;
                        captured = CapturedSymbolKeys(method, _graph.GetLocalFunctionControlFlowGraphInScope(method, _cancellationToken));
                    }
                    else
                    {
                        targetMethod = SymbolNames.Method(methodReference.Method);
                        target = methodReference.Method;
                        // A method group named through `base` binds the base member itself, as a base call runs it (R1).
                        isNonVirtual = !IsVirtualAccess(methodReference) && CallKind(target) == IrCallKind.Virtual;
                    }
                    receiver = methodReference.Instance is null ? null : LowerValue(methodReference.Instance);
                    break;
            }

            _operations.Add(new IrCreateDelegateOperation(
                NextOperation(), result, targetBodyId, targetMethod, receiver,
                Provenance(delegateCreation, "delegate-creation"))
            {
                CapturedSymbolKeys = captured,
                SiteOrdinal = _context.SiteOrdinals.Of(delegateCreation),
                TargetMethodId = target is null ? null : RootBodyId(target.OriginalDefinition),
                TargetContainingTypeKey = target is null ? null : SymbolNames.TypeKey(target.ContainingType),
                TargetMethodTypeArgumentKeys = target is null ? [] : target.TypeArguments.Select(SymbolNames.TypeKey).ToArray(),
                IsNonVirtual = isNonVirtual
            });
            return result;
        }

        /// <summary>The keys of the variables the body of <paramref name="function"/> captures, in the order its capture
        /// operations name them.</summary>
        /// <param name="function">The lambda or local function whose captures are listed.</param>
        /// <param name="graph">The function's control flow graph.</param>
        private IReadOnlyList<string> CapturedSymbolKeys(IMethodSymbol function, ControlFlowGraph graph) =>
            SsaPlan.Create(function, graph, EffectiveFlowGraph.Create(graph))
                   .CapturedVariables
                   .Select(variable => SymbolKey(variable.Symbol!))
                   .ToArray();

        private int Constant(IOperation operation, string transformation) =>
            Constant(operation, operation.ConstantValue.HasValue ? operation.ConstantValue.Value : null, transformation);

        private int Constant(IOperation operation, object? value, string transformation)
        {
            var name = value is null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
            return AddValue(IrValueKind.Constant, operation.Type, name, 0);
        }

        private int Unknown(IOperation operation, string reason) => Unknown(operation, reason, []);

        private int Unknown(IOperation operation, string reason, IReadOnlyList<int> operands, string? dynamicCallee = null)
        {
            int? result = operation.Type is null || operation.Type.SpecialType == SpecialType.System_Void
                ? null
                : AddTemporary(operation.Type);
            _operations.Add(new IrUnknownOperation(
                NextOperation(), result, operation.Kind.ToString(), reason, operands, Provenance(operation, reason))
            {
                DynamicCallee = dynamicCallee
            });
            return result ?? Constant(operation, null, reason);
        }

        /// <summary>A <c>dynamic</c> call: the receiver and the arguments are its operands, and the member it names is part of its
        /// callee, so a call of <c>d.Read()</c> is never taken for a read of a member <c>Read</c> (R4).</summary>
        /// <param name="invocation">The dynamic invocation.</param>
        private int LowerDynamicInvocation(IDynamicInvocationOperation invocation)
        {
            var (receiver, callee) = invocation.Operation is IDynamicMemberReferenceOperation member
                ? (DynamicReceiver(member), $"dynamic invoke {member.MemberName}")
                : ([LowerValue(invocation.Operation)], "dynamic invoke");
            return Unknown(invocation, "unsupported", [.. receiver, .. invocation.Arguments.Select(LowerValue)], callee);
        }

        private int LowerDynamicRead(IOperation operation)
        {
            var read = DynamicTarget(operation)!.Value;
            return Unknown(operation, "unsupported", read.Operands, read.Get);
        }

        private int[] DynamicReceiver(IDynamicMemberReferenceOperation member) => member.Instance is null ? [] : [LowerValue(member.Instance)];

        /// <summary>A member or an indexer of a <c>dynamic</c> receiver as the target of an assignment: its receiver and indices, lowered
        /// once, and what its read and its write are called (R4). Null for every other target.</summary>
        /// <param name="target">The assignment's target.</param>
        private (int[] Operands, string Get, string Set)? DynamicTarget(IOperation target) => target switch
        {
            IDynamicMemberReferenceOperation member => (DynamicReceiver(member), $"dynamic get {member.MemberName}", $"dynamic set {member.MemberName}"),
            IDynamicIndexerAccessOperation indexer => ([LowerValue(indexer.Operation), .. indexer.Arguments.Select(LowerValue)],
                                                       "dynamic index get", "dynamic index set"),
            _ => null
        };

        // An operation the lowering does not model still evaluates its children: their loads, stores, calls and
        // delegate creations are lowered first, in evaluation order, and become the operands of the unknown.
        private int LowerUnsupported(IOperation operation)
        {
            var operands = operation.ChildOperations.Select(LowerValue).ToArray();
            return Unknown(operation, "unsupported", operands);
        }

        private bool TryLocation(IOperation target, out FieldLocation location)
        {
            target = UnwrapTarget(target);
            switch (target)
            {
                case IFieldReferenceOperation field:
                    location = GetFieldLocation(field);
                    return true;
                case IEventReferenceOperation reference when FieldLikeEvents.Is(reference.Event):
                    location = GetEventLocation(reference);
                    return true;
                case IPropertyReferenceOperation property when IsAutomatic(property.Property):
                    location = GetPropertyLocation(property);
                    return true;
                case IParameterReferenceOperation parameter when IsPrimaryConstructorParameter(parameter.Parameter):
                    location = GetPrimaryConstructorParameterLocation(parameter.Parameter);
                    return true;
                default:
                    location = default;
                    return false;
            }
        }

        private FieldLocation GetFieldLocation(IFieldReferenceOperation reference) =>
            new(reference.Instance is null ? null : LowerValue(reference.Instance), FieldRef(reference.Field));

        /// <summary>The storage a reference to a field-like event names: Roslyn lets the event stand for its field only inside its
        /// declaring type, where every reference that is not a subscription is one (ADR 0014).</summary>
        /// <param name="reference">The reference to a field-like event.</param>
        private FieldLocation GetEventLocation(IEventReferenceOperation reference) =>
            new(reference.Instance is null ? null : LowerValue(reference.Instance), FieldLikeEvents.FieldRef(reference.Event));

        private FieldLocation GetPropertyLocation(IPropertyReferenceOperation reference) =>
            new(reference.Instance is null ? null : LowerValue(reference.Instance), PropertyField(reference.Property));

        /// <summary>Whether a property reference names one cell of its receiver's storage rather than a value the receiver
        /// computes: an indexer that hands back a reference hands back the cell itself, which is what <c>Span&lt;T&gt;</c> and
        /// every other ref-returning indexer do. Anything a caller can assign through is storage, and storage of a receiver is
        /// an element of it (TD-043).</summary>
        /// <param name="property">The property reference to test.</param>
        private static bool IsElementIndexer(IPropertyReferenceOperation property) =>
            property is { Property.IsIndexer: true, Property.RefKind: RefKind.Ref or RefKind.RefReadOnly, Instance: not null } &&
            property.Arguments.Length != 0;

        /// <summary>Whether the index of such an indexer names one cell of the receiver: proven for the types
        /// <see cref="SpanTypes"/> knows and for nothing else, since a foreign indexer may hand back one cell for every index it
        /// is given, and numbering its cells would take a real pair away as two disjoint ones (TD-043).</summary>
        /// <param name="property">The element indexer reference.</param>
        private static bool NamesOneCell(IPropertyReferenceOperation property) =>
            SpanTypes.Names(Bcl.TypeName(property.Property.ContainingType));

        private bool IsAutomatic(IPropertySymbol property)
        {
            if (property.IsVirtual || property.IsAbstract || property.IsOverride ||
                property.ContainingType.TypeKind == TypeKind.Interface ||
                !property.Locations.Any(location => location.IsInSource))
            {
                return false;
            }

            var declarations = property.DeclaringSyntaxReferences
                                       .Select(reference => reference.GetSyntax(_cancellationToken))
                                       .OfType<PropertyDeclarationSyntax>()
                                       .ToArray();
            return declarations.Length != 0 && declarations.All(declaration =>
                declaration.AccessorList is { Accessors.Count: > 0 } accessors &&
                accessors.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null));
        }

        private int GetSymbolValue(ISymbol symbol)
        {
            if (_symbolValues.TryGetValue(symbol, out var existing))
                return existing;

            if (!_ssaPlan.TryGetVariable(symbol, out var variable))
                throw new InvalidOperationException($"No SSA variable was planned for '{symbol.Name}'.");
            var value = ResolveToken(_ssaPlan.Entry(_currentBlockOrdinal, variable));
            SetCurrent(variable, value);
            return value;
        }

        private SsaDefinitionToken NextDefinition(SsaVariable variable)
        {
            var position = _definitionPositions.GetValueOrDefault(variable);
            var definitions = _ssaPlan.Definitions(_currentBlockOrdinal)
                                      .Where(definition => definition.Variable == variable)
                                      .ToArray();
            if (position >= definitions.Length)
                throw new InvalidOperationException($"No SSA definition was planned for '{variable.Name}'.");
            _definitionPositions[variable] = position + 1;
            return definitions[position].Token;
        }

        private void SetCurrent(SsaVariable variable, int value)
        {
            if (variable.Symbol is not null)
                _symbolValues[variable.Symbol] = value;
            if (variable.Capture is CaptureId capture)
                _captureValues[capture] = value;
        }

        private int ResolveToken(SsaToken token)
        {
            if (_tokenValues.TryGetValue(token, out var existing))
                return existing;

            int value;
            switch (token)
            {
                case SsaDefaultToken:
                    value = AddValue(IrValueKind.Constant, null, "default", 0);
                    break;
                case SsaExceptionalToken:
                    value = AddValue(IrValueKind.Constant, null, "exceptional", 0);
                    break;
                case SsaDefinitionToken definition:
                    value = AddSsaValue(definition.Variable);
                    break;
                case SsaPhiToken phi:
                    value = AddSsaValue(phi.Variable);
                    break;
                case SsaIncomingToken incoming:
                    throw new InvalidOperationException($"Incoming value for '{incoming.Variable.Name}' was not initialized.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(token), token.GetType().FullName);
            }

            _tokenValues.Add(token, value);
            return value;
        }

        /// <summary>Versions count per variable, and per symbol across the graphs of one body.</summary>
        /// <param name="variable">The SSA variable a new version is defined for.</param>
        private int AddSsaValue(SsaVariable variable)
        {
            int version;
            if (variable.Symbol is { } symbol)
            {
                version = _symbolVersions.GetValueOrDefault(symbol) + 1;
                _symbolVersions[symbol] = version;
            }
            else
            {
                version = _ssaVersions.GetValueOrDefault(variable) + 1;
                _ssaVersions[variable] = version;
            }

            return AddValue(variable.Kind, variable.Type, variable.Name, version, KeyOf(variable));
        }

        private int AddTemporary(ITypeSymbol? type) =>
            AddValue(IrValueKind.Temporary, type, $"t{_nextValueId}", 0);

        private int AddValue(IrValueKind kind, ITypeSymbol? type, string name, int version, string? symbolKey = null)
        {
            var id = _nextValueId++;
            _values.Add(new IrValue(id, kind, TypeName(type), name, version) { SymbolKey = symbolKey });
            return id;
        }

        private string? KeyOf(SsaVariable variable) => variable.Symbol is null ? null : SymbolKey(variable.Symbol);

        /// <summary>A local's or parameter's identity: the body declaring it, its name and its declaration's span start.</summary>
        /// <param name="symbol">The local or parameter.</param>
        private string SymbolKey(ISymbol symbol)
        {
            var declaringBody = symbol.ContainingSymbol is IMethodSymbol containing && _nestedIds.TryGetValue(containing, out var nestedId)
                ? nestedId
                : _context.RootBodyId;
            var start = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start ?? -1;
            return string.Create(CultureInfo.InvariantCulture, $"{declaringBody}|{symbol.Name}|{start}");
        }

        private int NextOperation() => _nextOperationId++;

        private IrProvenance Provenance(IOperation operation, string transformation) => Provenance(operation.Syntax, transformation);

        private IrProvenance Provenance(SyntaxNode syntax, string transformation)
        {
            var lineSpan = syntax.GetLocation().GetLineSpan();
            var fullPath = Path.GetFullPath(lineSpan.Path);
            var relativePath = Path.GetRelativePath(_rootDirectory, fullPath);
            var outsideRoot = relativePath == ".." ||
                              relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                              Path.IsPathRooted(relativePath);
            var path = (outsideRoot ? fullPath : relativePath).Replace('\\', '/');
            var span = new SourceSpan(
                path,
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1,
                lineSpan.EndLinePosition.Line + 1,
                lineSpan.EndLinePosition.Character + 1);
            return new IrProvenance(span, SymbolNames.Method(_method), syntax.Kind().ToString(), transformation);
        }

        private static IrBodyKind BodyKind(IMethodSymbol method) => method.MethodKind switch
        {
            MethodKind.AnonymousFunction => IrBodyKind.Lambda,
            MethodKind.LocalFunction => IrBodyKind.LocalFunction,
            _ => IrBodyKind.Method
        };

        private static IrRefKind RefKindOf(RefKind kind) => kind switch
        {
            RefKind.Ref => IrRefKind.Ref,
            RefKind.Out => IrRefKind.Out,
            RefKind.In => IrRefKind.In,
            RefKind.RefReadOnlyParameter => IrRefKind.RefReadOnly,
            _ => IrRefKind.None
        };

        private static IrBlockKind BlockKind(BasicBlockKind kind) => kind switch
        {
            BasicBlockKind.Entry => IrBlockKind.Entry,
            BasicBlockKind.Block => IrBlockKind.Block,
            BasicBlockKind.Exit => IrBlockKind.Exit,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        private static IrRegionKind RegionKind(ControlFlowRegionKind kind) => kind switch
        {
            ControlFlowRegionKind.Root => IrRegionKind.Root,
            ControlFlowRegionKind.LocalLifetime => IrRegionKind.LocalLifetime,
            ControlFlowRegionKind.StaticLocalInitializer => IrRegionKind.StaticLocalInitializer,
            ControlFlowRegionKind.ErroneousBody => IrRegionKind.ErroneousBody,
            ControlFlowRegionKind.TryAndFinally => IrRegionKind.TryAndFinally,
            ControlFlowRegionKind.TryAndCatch => IrRegionKind.TryAndCatch,
            ControlFlowRegionKind.FilterAndHandler => IrRegionKind.FilterAndHandler,
            ControlFlowRegionKind.Filter => IrRegionKind.Filter,
            ControlFlowRegionKind.Try => IrRegionKind.Try,
            ControlFlowRegionKind.Catch => IrRegionKind.Catch,
            ControlFlowRegionKind.Finally => IrRegionKind.Finally,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        private static IrBranchKind BranchKind(ControlFlowBranchSemantics semantics) => semantics switch
        {
            ControlFlowBranchSemantics.None => IrBranchKind.None,
            ControlFlowBranchSemantics.Regular => IrBranchKind.Regular,
            ControlFlowBranchSemantics.Return => IrBranchKind.Return,
            ControlFlowBranchSemantics.StructuredExceptionHandling => IrBranchKind.StructuredExceptionHandling,
            ControlFlowBranchSemantics.ProgramTermination => IrBranchKind.ProgramTermination,
            ControlFlowBranchSemantics.Throw => IrBranchKind.Throw,
            ControlFlowBranchSemantics.Rethrow => IrBranchKind.Rethrow,
            ControlFlowBranchSemantics.Error => IrBranchKind.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(semantics), semantics, null)
        };

        private static IrCallKind CallKind(IMethodSymbol method)
        {
            if (method.MethodKind == MethodKind.LocalFunction)
                return IrCallKind.LocalFunction;
            if (method.MethodKind == MethodKind.DelegateInvoke)
                return IrCallKind.Delegate;
            if (method.MethodKind == MethodKind.Constructor)
                return IrCallKind.Constructor;
            if (method.IsStatic)
                return IrCallKind.Static;
            if (method.ContainingType.TypeKind == TypeKind.Interface)
                return IrCallKind.Interface;
            if (method.IsVirtual || method.IsAbstract || method.IsOverride)
                return IrCallKind.Virtual;
            return IrCallKind.Instance;
        }

        private static string TypeName(ITypeSymbol? type) => type is null ? "?" : SymbolNames.Type(type);

        private static string? TypeKeyOf(ITypeSymbol? type) => type is null ? null : SymbolNames.TypeKey(type);

        private readonly record struct FieldLocation(int? Receiver, IrFieldRef Field);

        /// <summary>A lock a scope object holds until it is disposed.</summary>
        /// <param name="LockValue">The value of the primitive the scope holds.</param>
        /// <param name="Primitive">The kind of synchronization primitive.</param>
        /// <param name="Mode">The mode the primitive is held in.</param>
        private readonly record struct LockScope(int LockValue, IrSynchronizationPrimitive Primitive, IrLockMode Mode);

        /// <summary>An asynchronous entry waiting to be awaited; a conditional one holds the primitive only where the value the
        /// await produces is true.</summary>
        /// <param name="LockValue">The value of the primitive entered.</param>
        /// <param name="Primitive">The kind of synchronization primitive.</param>
        /// <param name="Mode">The mode the primitive is entered in.</param>
        /// <param name="IsConditional">Whether the entry holds the primitive only where the awaited result is true.</param>
        private readonly record struct PendingEntry(int LockValue, IrSynchronizationPrimitive Primitive, IrLockMode Mode,
                                                    bool IsConditional);

        private sealed record LoweredArguments(IReadOnlyList<int> Values, IReadOnlyList<int> Ordinals,
                                               IReadOnlyDictionary<int, int> RefResults)
        {
            internal static LoweredArguments None { get; } = new([], [], new Dictionary<int, int>());

            /// <summary>The value bound to the parameter with this ordinal, null where the call passes none. Arguments are
            /// lowered in the order they are written, for their side effects, and read by the parameter they are bound to: a
            /// named argument may be written in any order, so a position names a different parameter for every caller who
            /// writes them differently.</summary>
            /// <param name="ordinal">The ordinal of the parameter.</param>
            internal int? At(int ordinal)
            {
                for (var position = 0; position < Values.Count; position++)
                {
                    if ((position < Ordinals.Count ? Ordinals[position] : position) == ordinal)
                        return Values[position];
                }

                return null;
            }
        }
    }

    /// <summary>The recognizer of the lowering that claims the members of a top-level type by the type's metadata name alone, wherever
    /// the type is declared: <c>collections</c> (ADR 0010), <c>spawn</c>, <c>timer</c>, <c>lock</c>, <c>service-call</c>, or
    /// <c>recognized</c> for the other types whose calls are never unresolved (R1); <c>null</c> for a type none of them claims. The
    /// lowering itself claims members of metadata types only; the model generator asks this of a decompiled library's own types, whose
    /// calls in an analysis run would never reach a model (SPEC TD-034b).</summary>
    /// <param name="metadataName">The metadata name of a top-level type, such as <c>System.Collections.Generic.List`1</c>.</param>
    internal static string? RecognizerOf(string metadataName) =>
        Collections.ClaimsType(metadataName) ? "collections"
        : Bcl.IsSpawnType(metadataName) ? "spawn"
        : Bcl.IsTimerType(metadataName) ? "timer"
        : Synchronization.ClaimsType(metadataName) ? "lock"
        : ServiceCalls.ClaimsType(metadataName) ? "service-call"
        : LibraryModels.IsRecognizedType(metadataName) || SpanTypes.Names(metadataName) ? "recognized"
        : null;

    /// <summary>The recognizer of the lowering that claims a member of a type it does not claim whole, wherever the type is declared:
    /// <c>recognized</c> for <c>GetResult()</c> of a task's awaiter and <c>GetAwaiter()</c> of a configured awaitable (R3); <c>null</c>
    /// for every other member, <c>OnCompleted</c> and <c>UnsafeOnCompleted</c> among them, whose callbacks stay unresolved handoffs.
    /// The lowering's recognition and the generator's claims both ask it.</summary>
    /// <param name="method">The method, from metadata or from source.</param>
    internal static string? RecognizerOf(IMethodSymbol method) => Awaiters.Claims(method) ? "recognized" : null;

    /// <summary>The task, thread, parallel and timer members of the BCL the lowering recognizes, by the metadata name of the type
    /// declaring the called member. A type declared in source never matches, so a same-named user type is an ordinary call, and a
    /// derived type that does not override a member still calls the BCL one.</summary>
    private static class Bcl
    {
        internal const string TASK = "System.Threading.Tasks.Task";
        internal const string TASK_T = "System.Threading.Tasks.Task`1";
        internal const string FACTORY = "System.Threading.Tasks.TaskFactory";
        internal const string FACTORY_T = "System.Threading.Tasks.TaskFactory`1";
        internal const string EXTENSIONS = "System.Threading.Tasks.TaskExtensions";
        internal const string PARALLEL = "System.Threading.Tasks.Parallel";
        internal const string THREAD_POOL = "System.Threading.ThreadPool";
        internal const string THREAD = "System.Threading.Thread";
        internal const string TIMER = "System.Threading.Timer";
        internal const string WAIT_HANDLE = "System.Threading.WaitHandle";
        internal const string TIMERS_TIMER = "System.Timers.Timer";
        internal const string VALUE_TASK = "System.Threading.Tasks.ValueTask";
        internal const string VALUE_TASK_T = "System.Threading.Tasks.ValueTask`1";
        internal const string TCS = "System.Threading.Tasks.TaskCompletionSource";
        internal const string TCS_T = "System.Threading.Tasks.TaskCompletionSource`1";

        internal static string? TypeOf(ISymbol member) => Name(member.ContainingType);

        /// <summary>Whether a type is a task whose members give tasks the lowering follows to their completion values.</summary>
        /// <param name="type">The type's metadata name.</param>
        internal static bool IsTaskType(string type) => type is TASK or TASK_T or VALUE_TASK or VALUE_TASK_T;

        /// <summary>Whether a type is a task that completes with a value: <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c>.</summary>
        /// <param name="type">The type.</param>
        internal static bool CarriesValue(ITypeSymbol? type) => Name(type) is TASK_T or VALUE_TASK_T;

        /// <summary>Whether a type is one whose members the lowering recognizes, so a call of it in another form is unrecognized
        /// rather than an ordinary call.</summary>
        /// <param name="type">The type's metadata name.</param>
        internal static bool IsRecognizedType(string type) => IsSpawnType(type) || IsTimerType(type);

        /// <summary>Whether a type is one whose members start or join work.</summary>
        /// <param name="type">The type's metadata name.</param>
        internal static bool IsSpawnType(string type) =>
            type is TASK or TASK_T or FACTORY or FACTORY_T or EXTENSIONS or PARALLEL or THREAD_POOL or THREAD or WAIT_HANDLE;

        /// <summary>Whether a type is a timer whose callbacks the lowering starts.</summary>
        /// <param name="type">The type's metadata name.</param>
        internal static bool IsTimerType(string type) => type is TIMER or TIMERS_TIMER;

        internal static bool IsTask(ITypeSymbol? type, bool withValueTask) =>
            Name(type) is TASK or TASK_T || withValueTask && Name(type) is VALUE_TASK or VALUE_TASK_T;

        internal static bool IsSpan(ITypeSymbol type) => Name(type) == "System.ReadOnlySpan`1";

        /// <summary>Whether a delegate type, as the overload declares it, returns <c>Task</c> or <c>Task&lt;T&gt;</c>; a type
        /// parameter that a call binds to a task does not count.</summary>
        /// <param name="type">The delegate type, as the overload declares it.</param>
        internal static bool ReturnsTask(ITypeSymbol type) =>
            type is INamedTypeSymbol { DelegateInvokeMethod.ReturnType: var returned } && IsTask(returned, withValueTask: false);

        internal static bool IsAsyncDelegate(IOperation? value)
        {
            while (value is IConversionOperation or IParenthesizedOperation)
                value = value is IConversionOperation conversion ? conversion.Operand : ((IParenthesizedOperation)value).Operand;
            return value is IDelegateCreationOperation creation && creation.Target switch
            {
                IFlowAnonymousFunctionOperation function => function.Symbol.IsAsync,
                IAnonymousFunctionOperation function => function.Symbol.IsAsync,
                IMethodReferenceOperation reference => reference.Method.IsAsync,
                _ => false
            };
        }

        internal static IrTimerInterval Interval(IOperation? value) => value?.ConstantValue is { HasValue: true, Value: var constant }
            ? constant switch
            {
                int number => Interval(number),
                long number => Interval(number),
                uint number => number == uint.MaxValue ? IrTimerInterval.Infinite : Interval(number),
                _ => IrTimerInterval.Unknown
            }
            : IrTimerInterval.Unknown;

        private static IrTimerInterval Interval(long value) => value switch
        {
            0 => IrTimerInterval.Zero,
            -1 => IrTimerInterval.Infinite,
            > 0 => IrTimerInterval.Positive,
            _ => IrTimerInterval.Unknown
        };

        internal static IrTimerFlag Flag(IOperation? value) => value?.ConstantValue is { HasValue: true, Value: bool flag }
            ? flag ? IrTimerFlag.True : IrTimerFlag.False
            : IrTimerFlag.Unknown;

        /// <summary>The metadata name of a type declared outside source; null for a type of this compilation, so a same-named user
        /// type is never taken for the framework one.</summary>
        /// <param name="type">The type to name.</param>
        internal static string? TypeName(ITypeSymbol? type) => Name(type);

        private static string? Name(ITypeSymbol? type) =>
            type?.OriginalDefinition is INamedTypeSymbol { ContainingType: null } named && !named.Locations.Any(location => location.IsInSource)
                ? $"{named.ContainingNamespace.ToDisplayString()}.{named.MetadataName}"
                : null;
    }

    /// <summary>The awaiters of the four task types and the configured awaitables, whose members the lowering recognizes one by one:
    /// a recognized type would make every member of it recognized, and <c>OnCompleted</c> and <c>UnsafeOnCompleted</c> are handed
    /// callbacks that have to stay unresolved handoffs. Types are named by metadata name, a nested one after its containing type and
    /// a <c>+</c>.</summary>
    private static class Awaiters
    {
        private const string NAMESPACE = "System.Runtime.CompilerServices.";
        private const string CONFIGURED_TASK = NAMESPACE + "ConfiguredTaskAwaitable";
        private const string CONFIGURED_TASK_T = NAMESPACE + "ConfiguredTaskAwaitable`1";
        private const string CONFIGURED_VALUE_TASK = NAMESPACE + "ConfiguredValueTaskAwaitable";
        private const string CONFIGURED_VALUE_TASK_T = NAMESPACE + "ConfiguredValueTaskAwaitable`1";

        /// <summary>The awaiters, each with whether its <c>GetResult()</c> throws only after the task completes: a <c>Task</c>'s does, a
        /// <c>ValueTask</c>'s may throw before when an <c>IValueTaskSource</c> backs it (R3).</summary>
        private static readonly Dictionary<string, bool> ThrowsOnlyAfterCompletion = new(StringComparer.Ordinal)
        {
            [NAMESPACE + "TaskAwaiter"] = true,
            [NAMESPACE + "TaskAwaiter`1"] = true,
            [CONFIGURED_TASK + "+ConfiguredTaskAwaiter"] = true,
            [CONFIGURED_TASK_T + "+ConfiguredTaskAwaiter"] = true,
            [NAMESPACE + "ValueTaskAwaiter"] = false,
            [NAMESPACE + "ValueTaskAwaiter`1"] = false,
            [CONFIGURED_VALUE_TASK + "+ConfiguredValueTaskAwaiter"] = false,
            [CONFIGURED_VALUE_TASK_T + "+ConfiguredValueTaskAwaiter"] = false
        };

        /// <summary>Whether a member is one the lowering recognizes on these types, wherever the type is declared: <c>GetResult()</c>
        /// of an awaiter or <c>GetAwaiter()</c> of a configured awaitable.</summary>
        /// <param name="method">The method.</param>
        internal static bool Claims(IMethodSymbol method) => IsGetResult(method, out _) || IsConfiguredGetAwaiter(method);

        /// <summary>Whether a method is <c>GetResult()</c> of an awaiter, and whether it throws only after the task completes.</summary>
        /// <param name="method">The method.</param>
        /// <param name="throwsOnlyAfterCompletion">Whether it throws only after the task completes.</param>
        internal static bool IsGetResult(IMethodSymbol method, out bool throwsOnlyAfterCompletion)
        {
            throwsOnlyAfterCompletion = false;
            return method is { Name: "GetResult", Parameters.Length: 0 } && Name(method.ContainingType) is { } type &&
                   ThrowsOnlyAfterCompletion.TryGetValue(type, out throwsOnlyAfterCompletion);
        }

        /// <summary>Whether a method is <c>GetAwaiter()</c> of a configured awaitable, which gives an awaiter of the same task.</summary>
        /// <param name="method">The method.</param>
        internal static bool IsConfiguredGetAwaiter(IMethodSymbol method) =>
            method is { Name: "GetAwaiter", Parameters.Length: 0 } &&
            Name(method.ContainingType) is CONFIGURED_TASK or CONFIGURED_TASK_T or CONFIGURED_VALUE_TASK or CONFIGURED_VALUE_TASK_T;

        private static string? Name(INamedTypeSymbol? type)
        {
            if (type?.OriginalDefinition is not { } named)
                return null;
            var name = named.MetadataName;
            for (var outer = named.ContainingType; outer is not null; outer = outer.ContainingType)
                name = $"{outer.MetadataName}+{name}";
            return named.ContainingNamespace is { IsGlobalNamespace: false } ns ? $"{ns.ToDisplayString()}.{name}" : name;
        }
    }

    /// <summary>How a call enters or leaves a synchronization primitive (TD-080, TD-083). An unconditional entry holds it once
    /// control goes on normally; a conditional one, which is every entry with a timeout, holds it only where its success flag is
    /// true; the asynchronous forms do both at the point the result is awaited.</summary>
    private enum SyncEffectKind
    {
        Enter,
        TryEnter,
        EnterAsync,
        TryEnterAsync,
        Exit
    }

    private sealed record SyncEffect(SyncEffectKind Kind, IrSynchronizationPrimitive Primitive, IrLockMode Mode);

    /// <summary>The synchronization members the lowering models, by the primitive they belong to. A member that names no primitive,
    /// <c>SpinLock</c> and every unrecognized type among them, stays an ordinary call and is no protection (TD-086).</summary>
    private static class Synchronization
    {
        private const string MONITOR = "System.Threading.Monitor";
        private const string LOCK = "System.Threading.Lock";

        /// <summary>The type a <c>lock</c> statement names when it is not a monitor.</summary>
        internal const string LOCK_TYPE = LOCK;

        /// <summary>The parameter every <c>Monitor</c> member names its object in.</summary>
        internal const int MONITOR_OBJECT = 0;

        /// <summary>The parameter a conditional entry writes its success into: the <c>ref bool</c> the overload declares, which
        /// stands second on one overload and third on another, so it is found by what it is and never by where it is.</summary>
        /// <param name="method">The entry method called.</param>
        internal static int? SuccessFlag(IMethodSymbol method) =>
            method.Parameters.FirstOrDefault(parameter => parameter.RefKind == RefKind.Ref &&
                                                          parameter.Type.SpecialType == SpecialType.System_Boolean)?.Ordinal;
        private const string MUTEX = "System.Threading.Mutex";
        private const string SEMAPHORE_SLIM = "System.Threading.SemaphoreSlim";
        private const string READER_WRITER_LOCK_SLIM = "System.Threading.ReaderWriterLockSlim";

        internal static bool IsMonitor(IMethodSymbol method) => Bcl.TypeOf(method) == MONITOR;

        /// <summary>Whether a type is a primitive whose members this recognizer reads as entries and exits.</summary>
        /// <param name="type">The type's metadata name.</param>
        internal static bool ClaimsType(string type) => type is MONITOR or LOCK or MUTEX or SEMAPHORE_SLIM or READER_WRITER_LOCK_SLIM;

        /// <summary>What a call does, read from the type of the object it works on: <c>WaitOne</c> is declared by
        /// <c>WaitHandle</c>, so only the receiver says whether it is a mutex.</summary>
        /// <param name="method">The method called.</param>
        /// <param name="receiverType">The type of the call's receiver, if it has one.</param>
        internal static SyncEffect? EffectOf(IMethodSymbol method, ITypeSymbol? receiverType)
        {
            var type = IsMonitor(method) ? MONITOR : Bcl.TypeName(receiverType);
            var timed = HasTimeout(method);
            return (type, method.Name) switch
            {
                (MONITOR, "Enter") => new SyncEffect(SyncEffectKind.Enter, IrSynchronizationPrimitive.Monitor, IrLockMode.Exclusive),
                (MONITOR, "TryEnter") => new SyncEffect(SyncEffectKind.TryEnter, IrSynchronizationPrimitive.Monitor, IrLockMode.Exclusive),
                (MONITOR, "Exit") => new SyncEffect(SyncEffectKind.Exit, IrSynchronizationPrimitive.Monitor, IrLockMode.Exclusive),
                (LOCK, "Enter" or "EnterScope") => new SyncEffect(SyncEffectKind.Enter, IrSynchronizationPrimitive.Lock, IrLockMode.Exclusive),
                (LOCK, "TryEnter") => new SyncEffect(SyncEffectKind.TryEnter, IrSynchronizationPrimitive.Lock, IrLockMode.Exclusive),
                (LOCK, "Exit") => new SyncEffect(SyncEffectKind.Exit, IrSynchronizationPrimitive.Lock, IrLockMode.Exclusive),
                (MUTEX, "WaitOne") => new SyncEffect(timed ? SyncEffectKind.TryEnter : SyncEffectKind.Enter,
                                                     IrSynchronizationPrimitive.Mutex, IrLockMode.Exclusive),
                (MUTEX, "ReleaseMutex") => new SyncEffect(SyncEffectKind.Exit, IrSynchronizationPrimitive.Mutex, IrLockMode.Exclusive),
                (SEMAPHORE_SLIM, "Wait") => new SyncEffect(timed ? SyncEffectKind.TryEnter : SyncEffectKind.Enter,
                                                           IrSynchronizationPrimitive.SemaphoreSlim, IrLockMode.Exclusive),
                (SEMAPHORE_SLIM, "WaitAsync") => new SyncEffect(timed ? SyncEffectKind.TryEnterAsync : SyncEffectKind.EnterAsync,
                                                                IrSynchronizationPrimitive.SemaphoreSlim, IrLockMode.Exclusive),
                (SEMAPHORE_SLIM, "Release") => new SyncEffect(SyncEffectKind.Exit, IrSynchronizationPrimitive.SemaphoreSlim, IrLockMode.Exclusive),
                (READER_WRITER_LOCK_SLIM, var name) when ReaderWriterMode(name) is { } mode =>
                    new SyncEffect(name.StartsWith("Exit", StringComparison.Ordinal) ? SyncEffectKind.Exit
                                       : name.StartsWith("TryEnter", StringComparison.Ordinal) ? SyncEffectKind.TryEnter
                                       : SyncEffectKind.Enter,
                                   IrSynchronizationPrimitive.ReaderWriterLockSlim, mode),
                _ => null
            };
        }

        /// <summary>Whether the entry can come back without the primitive: every overload with a timeout can.</summary>
        /// <param name="method">The entry method called.</param>
        private static bool HasTimeout(IMethodSymbol method) =>
            method.Parameters.Any(parameter => parameter.Type.SpecialType == SpecialType.System_Int32 ||
                                               Bcl.TypeName(parameter.Type) == "System.TimeSpan");

        private static IrLockMode? ReaderWriterMode(string name) => name switch
        {
            "EnterReadLock" or "TryEnterReadLock" or "ExitReadLock" => IrLockMode.Read,
            "EnterWriteLock" or "TryEnterWriteLock" or "ExitWriteLock" => IrLockMode.Write,
            "EnterUpgradeableReadLock" or "TryEnterUpgradeableReadLock" or "ExitUpgradeableReadLock" => IrLockMode.UpgradeableRead,
            _ => null
        };

        /// <summary>How many permits an exit gives back: one for every primitive taken and given back once, including
        /// <c>SemaphoreSlim.Release()</c>; the constant a <c>Release(n)</c> names, read from the parameter that declares the count
        /// and never from the position it is written in; and null where that count is no constant (TD-083).</summary>
        /// <param name="invocation">The exit call, whose arguments give the count.</param>
        /// <param name="method">The exit method called.</param>
        internal static int? PermitsOf(IInvocationOperation invocation, IMethodSymbol method) =>
            Bcl.TypeName(method.ContainingType) != SEMAPHORE_SLIM || method.Name != "Release" || method.Parameters.Length == 0
                ? 1
                : invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value.ConstantValue
                      is { HasValue: true, Value: int permits }
                    ? permits
                    : null;

        /// <summary>The count a <c>SemaphoreSlim</c> was constructed with, when it is a constant: the parameter that declares it
        /// and never the argument that stands first, which a caller naming them may write anywhere (TD-083).</summary>
        /// <param name="creation">The object creation.</param>
        internal static int? CapacityOf(IObjectCreationOperation creation) =>
            Bcl.TypeName(creation.Type) == SEMAPHORE_SLIM &&
            creation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value.ConstantValue
                is { HasValue: true, Value: int capacity }
                ? capacity
                : null;
    }

    /// <summary>
    /// The collections modelled by their members (ADR 0010) and what each member does to the two resources of one: the structure,
    /// which is the collection itself, and the storage of its cells. A member that shifts its neighbours, scans for a value or
    /// enumerates touches cells it cannot name, so it takes no key argument and touches all of them. A thread-safe collection
    /// performs every member of it atomically on both resources; a <c>List</c>, a <c>Dictionary</c>, and since phase 5b a <c>HashSet</c>,
    /// a <c>Queue</c>, a <c>Stack</c> and a <c>LinkedList</c> perform none of them so.
    /// Every other type, and every member not listed here, stays an ordinary call.
    /// </summary>
    internal static class Collections
    {
        private const string LIST = "System.Collections.Generic.List`1";
        private const string DICTIONARY = "System.Collections.Generic.Dictionary`2";
        private const string CONCURRENT_DICTIONARY = "System.Collections.Concurrent.ConcurrentDictionary`2";
        private const string CONCURRENT_QUEUE = "System.Collections.Concurrent.ConcurrentQueue`1";
        private const string CONCURRENT_STACK = "System.Collections.Concurrent.ConcurrentStack`1";
        private const string CONCURRENT_BAG = "System.Collections.Concurrent.ConcurrentBag`1";
        private const string HASH_SET = "System.Collections.Generic.HashSet`1";
        private const string QUEUE = "System.Collections.Generic.Queue`1";
        private const string STACK = "System.Collections.Generic.Stack`1";
        private const string LINKED_LIST = "System.Collections.Generic.LinkedList`1";
        private const string LINKED_LIST_NODE = "System.Collections.Generic.LinkedListNode`1";
        private const string KEY_VALUE_PAIR = "System.Collections.Generic.KeyValuePair`2";
        private const string DICTIONARY_ENTRY = "System.Collections.DictionaryEntry";
        private const string KEY_COLLECTION = "System.Collections.Generic.Dictionary`2+KeyCollection";
        private const string VALUE_COLLECTION = "System.Collections.Generic.Dictionary`2+ValueCollection";
        private const string KEYS = "[keys]";
        private const string ELEMENT = "[]";
        private const string ARRAY = "System.Array";

        internal static IrCollectionCall? Of(IMethodSymbol method)
        {
            // A live view of a dictionary is enumerated and counted as the dictionary's own members are; any other member of it stays
            // an ordinary call, which no name branch below may claim (ADR 0010, phase 5b second run).
            if (ViewOf(method) is { } view)
            {
                return method.Name switch
                {
                    "GetEnumerator" => Effects(method, view, false, IrCollectionEffect.Read, IrCollectionEffect.Read),
                    "get_Count" => Effects(method, view, false, IrCollectionEffect.Read, IrCollectionEffect.None),
                    _ => null
                };
            }

            if (Bcl.TypeOf(method) is not { } type || !IsModelled(type))
                return null;
            // A pair is a copy of a key and a value: reading either touches no collection.
            if (type == KEY_VALUE_PAIR)
            {
                return method.Name is ".ctor" or "get_Key" or "get_Value" or "Deconstruct"
                    ? Effects(method, type, false, IrCollectionEffect.None, IrCollectionEffect.None)
                    : null;
            }
            // An entry is what the non-generic `IDictionary` hands out for a pair, and its key and value are read as the pair's are
            // (ADR 0010, amendment of the phase 5b third run).
            if (type == DICTIONARY_ENTRY)
            {
                return method.Name is "get_Key" or "get_Value"
                    ? Effects(method, type, false, IrCollectionEffect.None, IrCollectionEffect.None)
                    : null;
            }

            var keyed = type is DICTIONARY or CONCURRENT_DICTIONARY || type == LIST && method.Name is "get_Item" or "set_Item";
            return (type, method.Name) switch
            {
                // A view of a `Dictionary` is live: taking one touches nothing. One of a `ConcurrentDictionary` is a snapshot taken
                // atomically, which reads the structure and every cell.
                (DICTIONARY, "get_Keys" or "get_Values") => Effects(method, type, false, IrCollectionEffect.None, IrCollectionEffect.None) with
                {
                    View = method.Name == "get_Keys" ? KEYS : ELEMENT
                },
                (CONCURRENT_DICTIONARY, "get_Keys" or "get_Values") => Effects(method, type, false, IrCollectionEffect.Read,
                                                                               IrCollectionEffect.Read) with
                {
                    View = method.Name == "get_Keys" ? KEYS : ELEMENT
                },
                // A copy from a collection: `AddRange` is an insertion at an index nobody names, and a constructor makes a collection
                // nothing touches yet. Each enumerates its source; a constructor without one copies nothing.
                (LIST, "AddRange") => Effects(method, type, false, IrCollectionEffect.Write, IrCollectionEffect.Write) with
                {
                    Source = SourceOf(method)
                },
                (not LINKED_LIST_NODE, ".ctor") => Effects(method, type, false, IrCollectionEffect.None, IrCollectionEffect.None) with
                {
                    Source = SourceOf(method)
                },
                // A node is a cell of its list: its value is that cell, and moving to a neighbour reads the list's structure, which
                // setting a value leaves alone (ADR 0010, phase 5b).
                // A node created on its own holds its value, and touches no list until one is handed it.
                (LINKED_LIST_NODE, ".ctor") => Effects(method, type, false, IrCollectionEffect.None, IrCollectionEffect.None),
                (LINKED_LIST_NODE, "get_Value") => Effects(method, type, false, IrCollectionEffect.None, IrCollectionEffect.Read),
                (LINKED_LIST_NODE, "set_Value") => Effects(method, type, false, IrCollectionEffect.None, IrCollectionEffect.Write),
                (LINKED_LIST_NODE, "get_Next" or "get_Previous") => Effects(method, type, false, IrCollectionEffect.Read, IrCollectionEffect.None) with
                {
                    HandsOutCell = true
                },
                (LINKED_LIST, "AddFirst" or "AddLast" or "AddBefore" or "AddAfter") => Effects(method, type, false, IrCollectionEffect.Write,
                                                                                               IrCollectionEffect.Write) with
                {
                    HandsOutCell = true
                },
                (LINKED_LIST, "get_First" or "get_Last") => Effects(method, type, false, IrCollectionEffect.Read, IrCollectionEffect.None) with
                {
                    HandsOutCell = true
                },
                (LINKED_LIST, "Find" or "FindLast") => Effects(method, type, false, IrCollectionEffect.Read, IrCollectionEffect.Read) with
                {
                    HandsOutCell = true
                },
                (_, "Add" or "TryAdd" or "Enqueue" or "Push" or "set_Item") => Effects(method, type, keyed, IrCollectionEffect.Write,
                                                                                      IrCollectionEffect.Write),
                (LIST, "Insert" or "RemoveAt" or "Remove") => Effects(method, type, false, IrCollectionEffect.Write, IrCollectionEffect.Write),
                (_, "Remove" or "TryRemove") => Effects(method, type, keyed, IrCollectionEffect.Write, IrCollectionEffect.Write),
                (_, "TryDequeue" or "TryPop" or "TryTake" or "Dequeue" or "Pop" or "RemoveFirst" or "RemoveLast") =>
                    Effects(method, type, false, IrCollectionEffect.Write, IrCollectionEffect.Write),
                (_, "Clear") => Effects(method, type, false, IrCollectionEffect.Write, IrCollectionEffect.Write),
                (CONCURRENT_DICTIONARY, "GetOrAdd" or "AddOrUpdate") => Effects(method, type, true, IrCollectionEffect.Write,
                                                                                IrCollectionEffect.ReadWrite),
                (CONCURRENT_DICTIONARY, "TryUpdate") => Effects(method, type, true, IrCollectionEffect.Read, IrCollectionEffect.ReadWrite),
                (_, "get_Item" or "TryGetValue") => Effects(method, type, keyed, IrCollectionEffect.Read, IrCollectionEffect.Read),
                (_, "TryPeek" or "Peek") => Effects(method, type, false, IrCollectionEffect.Read, IrCollectionEffect.Read),
                // A key lookup never reaches a value, so it reads no cell, while a scan over the values reads every one of them.
                // Its key is carried all the same: a check that names a cell decides where the sequence it guards is reported.
                (_, "get_Count" or "ContainsKey") => Effects(method, type, keyed, IrCollectionEffect.Read, IrCollectionEffect.None),
                (_, "Contains" or "GetEnumerator") => Effects(method, type, false, IrCollectionEffect.Read, IrCollectionEffect.Read),
                // A member that takes a delegate runs it where the call stands, handed what the cells hold; it reads the structure and
                // every cell it visits, and one that removes or moves cells writes both as `Clear` does. Only the `Sort` that takes a
                // `Comparison<T>` is one of them (ADR 0010, phase 5c).
                (LIST, "ForEach" or "Exists" or "TrueForAll" or "Find" or "FindLast" or "FindIndex" or "FindLastIndex") =>
                    Effects(method, type, false, IrCollectionEffect.Read, IrCollectionEffect.Read),
                (LIST, "FindAll" or "ConvertAll") => Effects(method, type, false, IrCollectionEffect.Read, IrCollectionEffect.Read) with
                {
                    ResultTypeKey = SymbolNames.TypeKey(method.ReturnType)
                },
                (LIST, "RemoveAll") or (HASH_SET, "RemoveWhere") => Effects(method, type, false, IrCollectionEffect.Write, IrCollectionEffect.Write),
                (LIST, "Sort") when method.Parameters is [{ Type: var comparison }] && Bcl.TypeName(comparison) == "System.Comparison`1" =>
                    Effects(method, type, false, IrCollectionEffect.Write, IrCollectionEffect.Write),
                _ => null
            };
        }

        /// <summary>How a collection compares the keys of its cells, read from the comparer its <c>new</c> is given.</summary>
        /// <param name="creation">The collection's object creation.</param>
        internal static IrKeyEquality? EqualityOf(IObjectCreationOperation creation)
        {
            if (Bcl.TypeName(creation.Type) is not { } type || type is not (DICTIONARY or CONCURRENT_DICTIONARY))
                return null;
            var comparer = creation.Arguments.FirstOrDefault(argument => argument.Parameter?.Type.Name == "IEqualityComparer");
            return comparer?.Value switch
            {
                null => IrKeyEquality.Value,
                IConversionOperation { Operand: ILiteralOperation { ConstantValue.Value: null } } or ILiteralOperation { ConstantValue.Value: null } =>
                    IrKeyEquality.Value,
                // Only a comparer the analysis can decide for itself proves two keys apart, and that is the ordinal pair: one
                // compares the characters, the other folds their case. Every culture-sensitive comparer decides by collation,
                // which makes characters the analysis never reads ignorable — `"a­"` and `"a"` are one key under
                // `InvariantCulture`, measured on this runtime — so nothing about such keys is proven and the uncertainty is
                // reported instead (R7, ADR 0010).
                var value when Comparer(value) is { } name =>
                    name is "Ordinal" ? IrKeyEquality.Value
                        : name is "OrdinalIgnoreCase" ? IrKeyEquality.IgnoreCase
                        : IrKeyEquality.Unknown,
                _ => IrKeyEquality.Unknown
            };
        }

        /// <summary>The name of the <c>StringComparer</c> member a comparer argument names, null for anything else.</summary>
        /// <param name="value">The comparer argument's value.</param>
        private static string? Comparer(IOperation value) =>
            (value is IConversionOperation conversion ? conversion.Operand : value) is IPropertyReferenceOperation property &&
            Bcl.TypeName(property.Property.ContainingType) == "System.StringComparer"
                ? property.Property.Name
                : null;

        /// <summary>A member's effects, with the ordinal of the argument naming its cell when it names one: without it the member
        /// touches every cell of the collection.</summary>
        /// <param name="method">The member called.</param>
        /// <param name="type">The metadata name of the collection type.</param>
        /// <param name="keyed">Whether the member's first argument names its cell.</param>
        /// <param name="structure">The member's effect on the collection's structure.</param>
        /// <param name="element">The member's effect on the storage of its cells.</param>
        private static IrCollectionCall Effects(IMethodSymbol method, string type, bool keyed, IrCollectionEffect structure,
                                                IrCollectionEffect element) =>
            new($"{type}.{method.Name}", structure, element, keyed && method.Parameters.Length != 0 ? 0 : null,
                type is CONCURRENT_DICTIONARY or CONCURRENT_QUEUE or CONCURRENT_STACK or CONCURRENT_BAG)
            {
                Factories = method.OriginalDefinition.Parameters.Where(parameter => parameter.Type.TypeKind == TypeKind.Delegate)
                                  .Select(parameter => parameter.Ordinal)
                                  .ToArray(),
                // A factory's parameter of the dictionary's first type parameter is the key, of its second the value the dictionary
                // holds, and of the method's own the argument the overload hands it; one of a list's or a set's type parameter is
                // what its cells hold.
                FactoryInputs = method.OriginalDefinition.Parameters.Where(parameter => parameter.Type.TypeKind == TypeKind.Delegate)
                                      .Select(parameter => (IReadOnlyList<IrFactoryInput>)(((INamedTypeSymbol)parameter.Type).DelegateInvokeMethod?.Parameters ?? [])
                                                  .Select(input => input.Type is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Type, Ordinal: var ordinal }
                                                              ? ordinal == 0 && type is DICTIONARY or CONCURRENT_DICTIONARY ? IrFactoryInput.Key : IrFactoryInput.Held
                                                              : IrFactoryInput.Argument)
                                                  .ToArray())
                                      .ToArray(),
                FactoryArgument = method.OriginalDefinition.Parameters
                                        .FirstOrDefault(parameter => parameter.Type is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method })?.Ordinal
            };

        /// <summary>Whether the collection table describes the members of a type, a dictionary's views among them.</summary>
        /// <param name="type">The metadata name of a top-level type.</param>
        internal static bool ClaimsType(string type) => IsModelled(type);

        private static bool IsModelled(string type) =>
            type is LIST or DICTIONARY or CONCURRENT_DICTIONARY or CONCURRENT_QUEUE or CONCURRENT_STACK or CONCURRENT_BAG or HASH_SET or QUEUE or
                STACK or LINKED_LIST or LINKED_LIST_NODE or KEY_VALUE_PAIR or DICTIONARY_ENTRY;

        /// <summary>The view type declaring a member, <c>KeyCollection</c> or <c>ValueCollection</c> of a <c>Dictionary</c>; null for
        /// every other type, the views' own enumerators among them.</summary>
        /// <param name="method">The member called.</param>
        private static string? ViewOf(IMethodSymbol method) =>
            method.ContainingType?.OriginalDefinition is { ContainingType: { } outer } view && Bcl.TypeName(outer) == DICTIONARY
                ? view.Name switch
                {
                    "KeyCollection" => KEY_COLLECTION,
                    "ValueCollection" => VALUE_COLLECTION,
                    _ => null
                }
                : null;

        /// <summary>The ordinal of the parameter an overload takes a collection by, which it copies; null for an overload that takes
        /// none, such as a capacity or a comparer alone.</summary>
        /// <param name="method">The overload called.</param>
        private static int? SourceOf(IMethodSymbol method) =>
            method.OriginalDefinition.Parameters.FirstOrDefault(parameter => IsSequence(parameter.Type))?.Ordinal;

        private static bool IsSequence(ITypeSymbol type) =>
            type.SpecialType != SpecialType.System_String &&
            (type.SpecialType == SpecialType.System_Collections_IEnumerable ||
             type.AllInterfaces.Any(@interface => @interface.SpecialType == SpecialType.System_Collections_IEnumerable));

        /// <summary>The types an interface call may be decided on, by the kind their objects are to the heap and by metadata name.</summary>
        private static readonly (string Kind, string MetadataName)[] Implementers =
        [
            .. new[] { LIST, DICTIONARY, CONCURRENT_DICTIONARY, CONCURRENT_QUEUE, CONCURRENT_STACK, CONCURRENT_BAG, HASH_SET, QUEUE, STACK, LINKED_LIST }
                   .Select(type => (type[..type.IndexOf('`')], type)),
            ("System.Collections.Generic.Dictionary.KeyCollection", "System.Collections.Generic.Dictionary`2+KeyCollection"),
            ("System.Collections.Generic.Dictionary.ValueCollection", "System.Collections.Generic.Dictionary`2+ValueCollection")
        ];

        /// <summary>
        /// What a call of an interface member is on each kind of object (ADR 0010, amendment of the phase 5b third run): on an object of a
        /// type of the table or a <c>Dictionary</c> view, the member that type implements it with, found through the type's interface
        /// map, an explicit implementation taken as the public member of the table it stands for; on a <c>ConcurrentDictionary</c>
        /// snapshot, a list's count and enumeration alone; on an array, what the same code does to the array directly. A kind whose
        /// member the table does not model is left out, and the call stays what a direct call of that member is on its objects.
        /// </summary>
        /// <param name="method">The interface member called.</param>
        /// <param name="compilation">The compilation providing interface maps and array members.</param>
        /// <param name="libraryModels">The scoped models, or the built-in models when omitted.</param>
        internal static IReadOnlyList<IrImplementation> ImplementationsOf(IMethodSymbol method, Compilation compilation, LibraryModels? libraryModels = null)
        {
            if (method is not { IsStatic: false, ContainingType: { TypeKind: TypeKind.Interface } called })
                return [];

            var implementations = new List<IrImplementation>();
            foreach (var (kind, metadataName) in Implementers)
            {
                if (compilation.GetTypeByMetadataName(metadataName) is { } type && ImplementationOf(method, called, type) is { } implementation)
                    implementations.Add(implementation with { Kind = kind });
            }

            // A snapshot is a list of its own that decides nothing but its count and its enumeration, whatever interface names them.
            if (implementations.FirstOrDefault(implementation => implementation.Kind == CollectionObjects.LIST) is { } listed &&
                listed.Member.Member is $"{LIST}.get_Count" or $"{LIST}.GetEnumerator")
            {
                implementations.Add(listed with { Kind = CollectionObjects.SNAPSHOT });
            }

            foreach (var (kind, rank) in new[] { (CollectionObjects.ARRAY, 1), (CollectionObjects.MULTIDIMENSIONAL_ARRAY, 2) })
            {
                var array = compilation.CreateArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_Object), rank);
                if (array.AllInterfaces.Any(@interface => SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition, called.OriginalDefinition)) &&
                    ArrayMember(method, called, rank, compilation, libraryModels ?? LibraryModels.BuiltIn) is { } member)
                {
                    implementations.Add(member with { Kind = kind });
                }
            }

            return implementations;
        }

        /// <summary>The member of the table <paramref name="type"/> implements an interface member with, as a direct call of it is; null
        /// where the type does not implement the interface or the table does not model the member.</summary>
        /// <param name="method">The interface member called.</param>
        /// <param name="called">The interface declaring the member, as the call names it.</param>
        /// <param name="type">The table type whose implementation is looked up.</param>
        private static IrImplementation? ImplementationOf(IMethodSymbol method, INamedTypeSymbol called, INamedTypeSymbol type)
        {
            foreach (var implemented in type.AllInterfaces.Where(@interface => SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition,
                                                                                                                   called.OriginalDefinition)))
            {
                var member = implemented.GetMembers(method.Name).OfType<IMethodSymbol>()
                                        .FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, method.OriginalDefinition));
                if (member is null || type.FindImplementationForInterfaceMember(member) is not IMethodSymbol implementation)
                    continue;

                // An accessor of an explicitly implemented property is explicit as a method is, whatever kind of method it is.
                var explicitly = implementation.ExplicitInterfaceImplementations.Length != 0;
                var target = explicitly ? PublicCounterpart(implementation, type) : implementation;
                if (target is null || Of(target) is not { } effects)
                    return null;

                // An explicit implementation names the cell the public member names only where it takes the same key: `Add` of a pair
                // names no cell, while the non-generic indexer of a list names the one its index does, and `Remove(key)` of a
                // `ConcurrentDictionary` the one `TryRemove(key, out value)` does.
                if (explicitly && effects.KeyArgument is int key &&
                    (key >= implementation.Parameters.Length || implementation.Parameters[key].Name != target.Parameters[key].Name))
                {
                    effects = effects with { KeyArgument = null };
                }

                return new IrImplementation("", effects)
                {
                    ViewType = effects.View is null ? null : ViewTypeOf(target, implemented, called),
                    TakesPair = explicitly && implementation.Parameters.Length == 1 && target.Parameters.Length > 1 &&
                                Bcl.TypeName(implementation.Parameters[0].Type) == KEY_VALUE_PAIR
                };
            }

            return null;
        }

        /// <summary>The public member of the table an explicit implementation stands for: the member of its type with the same name, or the
        /// one <see cref="Counterparts"/> names where the implementation calls a member of another name, and, where there are several, the
        /// one taking the implementation's parameters by name, then the one taking as many.</summary>
        /// <param name="implementation">The explicit interface implementation.</param>
        /// <param name="type">The table type declaring it.</param>
        private static IMethodSymbol? PublicCounterpart(IMethodSymbol implementation, INamedTypeSymbol type)
        {
            var name = implementation.Name[(implementation.Name.LastIndexOf('.') + 1)..];
            if (implementation.ExplicitInterfaceImplementations.FirstOrDefault() is { } implemented &&
                Counterparts.TryGetValue((Bcl.TypeName(type) ?? "", Bcl.TypeName(implemented.ContainingType) ?? "", name), out var counterpart))
            {
                name = counterpart;
            }

            var candidates = type.GetMembers(name).OfType<IMethodSymbol>()
                                 .Where(candidate => candidate is { IsStatic: false, DeclaredAccessibility: Accessibility.Public })
                                 .ToArray();
            bool Takes(IMethodSymbol candidate) =>
                candidate.Parameters.Length >= implementation.Parameters.Length &&
                implementation.Parameters.Select(parameter => parameter.Name)
                              .SequenceEqual(candidate.Parameters.Take(implementation.Parameters.Length).Select(parameter => parameter.Name));
            return candidates.FirstOrDefault(candidate => candidate.Parameters.Length == implementation.Parameters.Length && Takes(candidate)) ??
                   candidates.FirstOrDefault(Takes) ??
                   candidates.FirstOrDefault(candidate => candidate.Parameters.Length == implementation.Parameters.Length) ??
                   candidates.FirstOrDefault();
        }

        /// <summary>The public member an explicit implementation calls where its name is not the implementation's own, by the type, the
        /// interface and the member's name (ADR 0010, amendment of the phase 5b third run).</summary>
        private static readonly Dictionary<(string Type, string Interface, string Member), string> Counterparts = new()
        {
            [(DICTIONARY, "System.Collections.IDictionary", "Contains")] = "ContainsKey",
            [(CONCURRENT_DICTIONARY, "System.Collections.IDictionary", "Contains")] = "ContainsKey",
            [(CONCURRENT_DICTIONARY, "System.Collections.Generic.IDictionary`2", "Add")] = "TryAdd",
            [(CONCURRENT_DICTIONARY, "System.Collections.IDictionary", "Add")] = "TryAdd",
            [(CONCURRENT_DICTIONARY, "System.Collections.Generic.ICollection`1", "Add")] = "TryAdd",
            [(CONCURRENT_DICTIONARY, "System.Collections.Generic.IDictionary`2", "Remove")] = "TryRemove",
            [(CONCURRENT_DICTIONARY, "System.Collections.IDictionary", "Remove")] = "TryRemove",
            [(CONCURRENT_DICTIONARY, "System.Collections.Generic.ICollection`1", "Remove")] = "TryRemove",
            [(LINKED_LIST, "System.Collections.Generic.ICollection`1", "Add")] = "AddLast",
            [(CONCURRENT_QUEUE, "System.Collections.Concurrent.IProducerConsumerCollection`1", "TryAdd")] = "Enqueue",
            [(CONCURRENT_QUEUE, "System.Collections.Concurrent.IProducerConsumerCollection`1", "TryTake")] = "TryDequeue",
            [(CONCURRENT_STACK, "System.Collections.Concurrent.IProducerConsumerCollection`1", "TryAdd")] = "Push",
            [(CONCURRENT_STACK, "System.Collections.Concurrent.IProducerConsumerCollection`1", "TryTake")] = "TryPop",
            [(CONCURRENT_BAG, "System.Collections.Concurrent.IProducerConsumerCollection`1", "TryAdd")] = "Add"
        };

        /// <summary>The type of the view a <c>Keys</c> or <c>Values</c> member of <paramref name="implemented"/>'s type hands out, with the
        /// type arguments the call gives the interface.</summary>
        /// <param name="target">The public <c>Keys</c> or <c>Values</c> member implementing the call.</param>
        /// <param name="implemented">The interface as the type implements it.</param>
        /// <param name="called">The interface as the call names it, whose type arguments are substituted.</param>
        private static string ViewTypeOf(IMethodSymbol target, INamedTypeSymbol implemented, INamedTypeSymbol called)
        {
            var type = target.ContainingType.OriginalDefinition;
            var arguments = type.TypeParameters.Select(parameter => implemented.TypeArguments.IndexOf(parameter, 0, SymbolEqualityComparer.Default) is var ordinal and >= 0
                                                           ? called.TypeArguments[ordinal]
                                                           : parameter)
                                .ToArray();
            var constructed = type.Construct(arguments);
            var member = constructed.GetMembers(target.Name).OfType<IMethodSymbol>()
                                    .First(candidate => SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, target.OriginalDefinition));
            return SymbolNames.Type(member.ReturnType);
        }

        /// <summary>What a member of an interface an array implements is on an array of <paramref name="rank"/>, as the same code does to
        /// the array directly (ADR 0010, amendment of the phase 5b third run): the indexer of a single-dimensional array is its element,
        /// <c>Contains</c> and <c>IndexOf</c> read every cell as a list's <c>Contains</c> and a <c>foreach</c> over the array do, with the
        /// structure they read too, <c>Count</c> and <c>IsReadOnly</c> touch nothing, enumeration is a
        /// <c>foreach</c> over the array, and a member the array refuses by throwing has no effect — among them the indexer, <c>Contains</c>
        /// and <c>IndexOf</c> of a multi-dimensional one, which need rank one. <c>CopyTo</c> and the non-generic <c>Clear</c> carry
        /// the scoped model of the direct <c>System.Array</c> member; null for every other undecided member.</summary>
        /// <param name="method">The interface member called.</param>
        /// <param name="called">The interface declaring that member.</param>
        /// <param name="rank">The rank of the receiver array.</param>
        /// <param name="compilation">The compilation resolving the direct member.</param>
        /// <param name="libraryModels">The scoped models, with project precedence.</param>
        private static IrImplementation? ArrayMember(IMethodSymbol method, INamedTypeSymbol called, int rank, Compilation compilation,
                                                    LibraryModels libraryModels)
        {
            var generic = called.IsGenericType;
            if (method.Name == "CopyTo" && Bcl.TypeName(called) is "System.Collections.ICollection" or "System.Collections.Generic.ICollection`1" ||
                method.Name == "Clear" && Bcl.TypeName(called) == "System.Collections.IList")
            {
                var direct = compilation.GetSpecialType(SpecialType.System_Array).GetMembers(method.Name).OfType<IMethodSymbol>()
                    .Single(candidate => method.Name == "Clear" ? candidate.Parameters.Length == 1 :
                        candidate.Parameters.Length == 2 && candidate.Parameters[1].Type.SpecialType == SpecialType.System_Int32);
                if (LibraryCalls.Of(direct, libraryModels) is not { } library)
                    return null;
                return new IrImplementation("", new IrCollectionCall(SymbolNames.Method(direct), IrCollectionEffect.None, IrCollectionEffect.None, null, false))
                {
                    Library = library,
                    DeclaringTypeKey = SymbolNames.TypeKey(direct.ContainingType),
                    ReceiverParameterOrdinal = direct.IsStatic ? 0 : null
                };
            }
            (IrCollectionEffect Structure, IrCollectionEffect Element, bool Keyed)? effects = (method.Name, rank) switch
            {
                ("get_Item", 1) => (IrCollectionEffect.None, IrCollectionEffect.Read, true),
                ("set_Item", 1) => (IrCollectionEffect.None, IrCollectionEffect.Write, true),
                ("Contains" or "IndexOf", 1) => (IrCollectionEffect.Read, IrCollectionEffect.Read, false),
                ("GetEnumerator", _) => (IrCollectionEffect.Read, IrCollectionEffect.Read, false),
                ("get_Item" or "set_Item" or "Contains" or "IndexOf" or "get_Count" or "get_IsReadOnly" or "Add" or "Insert" or "Remove" or "RemoveAt", _) =>
                    (IrCollectionEffect.None, IrCollectionEffect.None, false),
                ("Clear", _) when generic => (IrCollectionEffect.None, IrCollectionEffect.None, false),
                _ => null
            };
            return effects is var (structure, element, keyed)
                ? new IrImplementation("", new IrCollectionCall($"{ARRAY}.{method.Name}", structure, element, keyed ? 0 : null, false))
                : null;
        }
    }

    /// <summary>The library model's word on a called member without a source declaration (TD-034a), with each effect
    /// bound to the ordinal of the parameter it names; a member of this compilation is never the library's.</summary>
    private static class LibraryCalls
    {
        internal static IrLibraryCall? Of(IMethodSymbol method, LibraryModels libraryModels)
        {
            var definition = (method.ReducedFrom ?? method).OriginalDefinition;
            if (LibraryModels.IsDeclaredInRun(definition) ||
                libraryModels.Find(definition) is not { } match)
                return null;
            // The parameters as the call names them: what an argument's elements are depends on the type arguments it was given.
            var typed = method.ReducedFrom is null ? method : definition;
            var effects = match.Effects.Select(effect => new IrLibraryEffect(effect.Kind switch
                                               {
                                                   LibraryEffectKind.DeepRead => IrLibraryEffectKind.DeepRead,
                                                   LibraryEffectKind.WriteArgument => IrLibraryEffectKind.WriteArgument,
                                                   LibraryEffectKind.WriteCells => IrLibraryEffectKind.WriteCells,
                                                   _ => throw new UnreachableException($"Unknown effect kind {effect.Kind}.")
                                               }, effect.Parameter == "this" ? IrLibraryCall.RECEIVER :
                                                   definition.Parameters.Single(parameter => parameter.Name == effect.Parameter).Ordinal))
                                   .ToArray();
            var fates = match.Fates.Select(fate =>
            {
                var parameter = typed.Parameters.Single(candidate => candidate.Name == fate.Parameter);
                var invoke = ((INamedTypeSymbol)parameter.Type).DelegateInvokeMethod!;
                var arity = invoke.Parameters.Length;
                var inputs = fate.Inputs ?? Enumerable.Repeat<IReadOnlyList<LibraryValue>>([], arity).ToArray();
                return new IrLibraryFate(parameter.Ordinal, fate.Kind switch
                {
                    LibraryFateKind.InvokeNow => IrFateKind.InvokeNow,
                    LibraryFateKind.Iterator => IrFateKind.Iterator,
                    LibraryFateKind.Holder => IrFateKind.Holder,
                    LibraryFateKind.Startup => IrFateKind.Startup,
                    LibraryFateKind.UnknownExecution => IrFateKind.UnknownExecution,
                    LibraryFateKind.NotRun => IrFateKind.NotRun,
                    _ => throw new UnreachableException($"Unknown fate kind {fate.Kind}.")
                }, fate.Holder switch
                {
                    LibraryHolderKind.Result => IrHolderKind.Result,
                    LibraryHolderKind.This => IrHolderKind.This,
                    null => null,
                    _ => throw new UnreachableException($"Unknown holder kind {fate.Holder}.")
                }, inputs.Select((input, index) => (IReadOnlyList<IrModelValue>)input.Select(value =>
                                      Value(typed, value, invoke.Parameters[index].Type)).ToArray()).ToArray());
            }).ToArray();
            return new IrLibraryCall(match.MemberId, match.Kind == LibraryMatchKind.Known, effects,
                                     match.Layer == ModelLayer.Project ? IrModelLayer.Project : IrModelLayer.BuiltIn,
                                     match.DeclaredOpaque)
            {
                Fates = fates,
                Keeps = match.Keeps.ToDictionary(keep => KeeperOrdinal(typed, keep.Key),
                                                 keep => (IReadOnlyList<IrModelValue>)keep.Value.Select(value => Value(typed, value)).ToArray()),
                KeeperTypeKeys = match.Keeps.Keys.Where(name => name != "result")
                    .Concat(match.Fates.SelectMany(fate => fate.Inputs ?? []).SelectMany(input => input).Concat(match.Result?.Leaf.Values ?? [])
                                 .Concat(match.Stores.Values.SelectMany(values => values)).Concat(match.Outputs.Values.SelectMany(output => output.Values))
                                 .Concat(match.Keeps.Values.SelectMany(values => values)).SelectMany(Keepers))
                    .Distinct(StringComparer.Ordinal).ToDictionary(name => KeeperOrdinal(typed, name), name =>
                        SymbolNames.TypeKey((name == "this" ? typed.ContainingType : Parameter(typed, name).Type).OriginalDefinition)),
                Stores = match.Stores.ToDictionary(store => store.Key == "this" ? IrLibraryCall.RECEIVER : Parameter(typed, store.Key).Ordinal,
                                                   store => (IReadOnlyList<IrModelValue>)store.Value.Select(value => Value(typed, value)).ToArray()),
                Outputs = match.Outputs.ToDictionary(output => Parameter(typed, output.Key).Ordinal, output => Result(typed, output.Value)),
                OutputTypeKeys = match.Outputs.Keys.ToDictionary(name => Parameter(typed, name).Ordinal, name => SymbolNames.TypeKey(Parameter(typed, name).Type)),
                Result = match.Result is { } result ? Result(typed, result) : null,
                // The type of the result's leaf at every depth of task(…); a holder of the result on a task stands in its innermost
                // completion value, so it is of the innermost type.
                ResultTypeKey = method.MethodKind == MethodKind.Constructor ? SymbolNames.TypeKey(method.ContainingType)
                    : method.ReturnsVoid ? null
                    : SymbolNames.TypeKey(Completion(method.ReturnType, match.Result?.TaskDepth ??
                                                                         (match.Fates.Any(fate => fate.Holder == LibraryHolderKind.Result)
                                                                             ? TaskTypes.Depth(method.ReturnType) : 0))),
                ReturnTaskDepth = method.MethodKind == MethodKind.Constructor || method.ReturnsVoid ? 0 : TaskTypes.Depth(method.ReturnType)
            };
        }

        /// <summary>What <paramref name="depth"/> levels of tasks around a type complete with.</summary>
        /// <param name="type">The type.</param>
        /// <param name="depth">How many task levels to look through.</param>
        private static ITypeSymbol Completion(ITypeSymbol type, int depth)
        {
            for (var level = 0; level < depth; level++)
                type = TaskTypes.CompletionType(type) ?? throw new UnreachableException($"{type} completes with no value at depth {level}.");
            return type;
        }

        private static IrLibraryResult Result(IMethodSymbol method, LibraryResult result) => result.Kind == LibraryResultKind.Task
            ? new IrLibraryResult(IrResultKind.Task, [], Result(method, result.Inner!))
            : new(result.Kind switch
            {
                LibraryResultKind.Sequence => IrResultKind.Sequence,
                LibraryResultKind.Collection => IrResultKind.Collection,
                LibraryResultKind.Dictionary => IrResultKind.Dictionary,
                LibraryResultKind.OneOf => IrResultKind.OneOf,
                LibraryResultKind.New => IrResultKind.New,
                _ => throw new UnreachableException($"Unknown result kind {result.Kind}.")
            }, result.Values.Select(value => Value(method, value)).ToArray());

        private static IrModelValue Value(IMethodSymbol method, LibraryValue value, ITypeSymbol? newType = null) => value switch
        {
            NewValue => new IrModelNew(SymbolNames.TypeKey(StaticType(method, value, newType)!)),
            Providers.LibraryModels.ThisValue => new IrModelThis(),
            KeptValue kept => new IrModelKept(KeeperOrdinal(method, kept.Keeper)),
            ArgumentValue argument => new IrModelArgument(Parameter(method, argument.Parameter).Ordinal),
            ReturnsValue returns => new IrModelReturns(Parameter(method, returns.Delegate).Ordinal),
            HolderArgumentValue holder => new IrModelHolderArgument(holder.Index),
            ElementsValue elements => new IrModelElements(Value(method, elements.Source), ElementTypeKey(StaticType(method, elements.Source))),
            SequenceValue sequence => new IrModelSequence(sequence.Values.Select(item => Value(method, item)).ToArray()),
            GroupingValue grouping => new IrModelGrouping(Value(method, grouping.Key), Value(method, grouping.Values)),
            CompletionValue completion => new IrModelCompletion(Value(method, completion.Source)),
            _ => throw new UnreachableException($"Unknown value kind {value.GetType().Name}.")
        };

        private static int KeeperOrdinal(IMethodSymbol method, string keeper) => keeper switch
        {
            "this" => IrLibraryCall.RECEIVER,
            "result" => IrLibraryCall.RESULT,
            _ => Parameter(method, keeper).Ordinal
        };

        private static IEnumerable<string> Keepers(LibraryValue value) => value switch
        {
            KeptValue kept => [kept.Keeper],
            ElementsValue elements => Keepers(elements.Source),
            SequenceValue sequence => sequence.Values.SelectMany(Keepers),
            GroupingValue grouping => Keepers(grouping.Key).Concat(Keepers(grouping.Values)),
            CompletionValue completion => Keepers(completion.Source),
            NewValue or ArgumentValue or ReturnsValue or HolderArgumentValue or Providers.LibraryModels.ThisValue => [],
            _ => throw new UnreachableException($"Unknown value kind {value.GetType().Name}.")
        };

        private static IParameterSymbol Parameter(IMethodSymbol method, string name) => method.Parameters.Single(parameter => parameter.Name == name);

        /// <summary>The type a value has as the call names it, null where it is none the model can say.</summary>
        /// <param name="method">The member as the caller names it.</param>
        /// <param name="value">The model value whose static type is requested.</param>
        /// <param name="newType">The delegate parameter type of a <c>new</c> input.</param>
        private static ITypeSymbol? StaticType(IMethodSymbol method, LibraryValue value, ITypeSymbol? newType = null) => value switch
        {
            NewValue => newType ?? throw new UnreachableException("A new input has no delegate parameter type."),
            Providers.LibraryModels.ThisValue => method.ContainingType,
            ArgumentValue argument => Parameter(method, argument.Parameter).Type,
            ReturnsValue returns => (Parameter(method, returns.Delegate).Type as INamedTypeSymbol)?.DelegateInvokeMethod?.ReturnType,
            ElementsValue elements => ElementType(StaticType(method, elements.Source)),
            CompletionValue completion => TaskTypes.CompletionType(StaticType(method, completion.Source)),
            SequenceValue or GroupingValue or HolderArgumentValue or KeptValue => null,
            _ => throw new UnreachableException($"Unknown value kind {value.GetType().Name}.")
        };

        private static ITypeSymbol? ElementType(ITypeSymbol? type) =>
            type is IArrayTypeSymbol array ? array.ElementType
            : type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } enumerable
                ? enumerable.TypeArguments[0]
            : type?.AllInterfaces.FirstOrDefault(@interface => @interface.OriginalDefinition.SpecialType ==
                                                               SpecialType.System_Collections_Generic_IEnumerable_T)?.TypeArguments[0];

        /// <summary>The type of what enumerating a value of <paramref name="source"/> yields, where it narrows what that may be: none for
        /// <c>object</c>, a type parameter or an unknown element type.</summary>
        /// <param name="source">The static type of the enumerated value.</param>
        private static string? ElementTypeKey(ITypeSymbol? source) =>
            ElementType(source) is { SpecialType: not SpecialType.System_Object } element and not ITypeParameterSymbol
                ? SymbolNames.TypeKey(element)
                : null;
    }

    /// <summary>The <c>Interlocked</c> and <c>Volatile</c> members that name one cell, with what each does to it (TD-082). The two
    /// memory barriers are deliberately absent: they name no cell, so they stay an ordinary call, which is neither an access nor a
    /// protection. Modelling barriers and reordering is not part of this version.</summary>
    private static class Atomics
    {
        private const string INTERLOCKED = "System.Threading.Interlocked";
        private const string VOLATILE = "System.Threading.Volatile";

        /// <summary>What an atomic mark on a <c>volatile</c> field's load or store is called.</summary>
        internal const string VOLATILE_FIELD = "volatile-field";

        /// <summary>The parameters every modelled member declares its cell, the value it writes and the value it checks against
        /// in. An argument is read by the parameter it is bound to and never by where the caller happened to write it.</summary>
        internal const int LOCATION = 0;

        internal const int VALUE = 1;

        internal const int COMPARAND = 2;

        internal static IArgumentOperation? ArgumentAt(IInvocationOperation invocation, int ordinal) =>
            invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == ordinal);

        internal static IrAtomicEffect? EffectOf(IMethodSymbol method) => (Bcl.TypeOf(method), method.Name) switch
        {
            (INTERLOCKED, "Read") or (VOLATILE, "Read") => IrAtomicEffect.Read,
            (VOLATILE, "Write") => IrAtomicEffect.Write,
            // The one member that writes on a condition: it is given the value it expects to find, and leaves the cell alone
            // where it finds another. Every other member writes whatever it was handed.
            (INTERLOCKED, "CompareExchange") => IrAtomicEffect.CompareAndSwap,
            (INTERLOCKED, "Increment" or "Decrement" or "Add" or "Exchange" or "And" or "Or") =>
                IrAtomicEffect.ReadModifyWrite,
            _ => null
        };
    }

    /// <summary>Recognises the service-locator and scope-creation calls of the DI semantics provider, reading the call's original
    /// operation tree: the constant service type, and the kind of provider the receiver syntactically is.</summary>
    private static class ServiceCalls
    {
        private const string SERVICE_PROVIDER = "System.IServiceProvider";
        private const string PROVIDER_EXTENSIONS = "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions";
        private const string SCOPE_FACTORY = "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory";
        private const string SERVICE_SCOPE = "Microsoft.Extensions.DependencyInjection.IServiceScope";
        private const string ASYNC_SERVICE_SCOPE = "Microsoft.Extensions.DependencyInjection.AsyncServiceScope";
        private const string HOST = "Microsoft.Extensions.Hosting.IHost";
        private const string HTTP_CONTEXT = "Microsoft.AspNetCore.Http.HttpContext";
        private const string APPLICATION_BUILDER = "Microsoft.AspNetCore.Builder.IApplicationBuilder";

        private static readonly SupportedAssemblyVersion HTTP_ABSTRACTIONS =
            SupportedAssemblyVersion.Framework("Microsoft.AspNetCore.Http.Abstractions");

        /// <summary>Whether a type declares locator or scope-creation members this recognizer reads.</summary>
        /// <param name="type">The type's metadata name.</param>
        internal static bool ClaimsType(string type) => type is SERVICE_PROVIDER or PROVIDER_EXTENSIONS or SCOPE_FACTORY;

        internal static IrServiceCall? Of(IInvocationOperation invocation, Compilation compilation, CancellationToken cancellationToken)
        {
            var method = invocation.TargetMethod;
            if (KindOf(method) is not { } kind)
                return null;

            var model = compilation.GetSemanticModel(invocation.Syntax.SyntaxTree);
            var original = model.GetOperation(invocation.Syntax, cancellationToken) as IInvocationOperation ?? invocation;
            var arguments = original.Arguments.Where(argument => argument.Parameter is not null)
                                    .OrderBy(argument => argument.Parameter!.Ordinal)
                                    .ToArray();
            string? key = null;
            if (kind != IrServiceCallKind.ScopeCreation && method.IsGenericMethod)
            {
                key = DiIndexBuilder.ContainsTypeParameter(method.TypeArguments[0]) ? null : SymbolNames.TypeKey(method.TypeArguments[0]);
            }
            else if (kind != IrServiceCallKind.ScopeCreation &&
                     arguments.FirstOrDefault(argument => DiIndexBuilder.IsSystemType(argument.Parameter!.Type)) is { } typeArgument &&
                     DiIndexBuilder.TypeOf(typeArgument.Value) is ({ } type, null))
            {
                key = SymbolNames.TypeKey(type);
            }

            var (receiver, receiverType) = method.IsStatic
                ? (arguments.FirstOrDefault()?.Value, method.Parameters[0].Type)
                : (original.Instance, (ITypeSymbol)method.ContainingType);
            var provider = receiver is not null && IsServiceProvider(receiverType) ? ProviderKind(receiver, model, cancellationToken) : null;
            return new IrServiceCall(kind, key, provider);
        }

        private static IrServiceCallKind? KindOf(IMethodSymbol method)
        {
            if (IsServiceProvider(method.ContainingType))
                return method is { Name: "GetService", IsStatic: false, Parameters.Length: 1 } ? IrServiceCallKind.Locator : null;
            if (ExactSymbols.IsType(method.ContainingType, DiIndexBuilder.DependencyInjection, PROVIDER_EXTENSIONS))
            {
                return method.Name switch
                {
                    "GetService" or "GetRequiredService" when method.IsGenericMethod || method.Parameters.Length == 2 => IrServiceCallKind.Locator,
                    "GetServices" when method.IsGenericMethod => IrServiceCallKind.LocatorAll,
                    "CreateScope" or "CreateAsyncScope" => IrServiceCallKind.ScopeCreation,
                    _ => null
                };
            }

            return method.Name == "CreateScope" && ExactSymbols.IsType(method.ContainingType, DiIndexBuilder.DependencyInjection, SCOPE_FACTORY)
                ? IrServiceCallKind.ScopeCreation
                : null;
        }

        /// <summary>The kind of provider a value is: a well-known provider property, a constructor parameter (used directly or
        /// through an instance field only constructors write from one), or a local whose only write is such an initializer.</summary>
        /// <param name="value">The receiver value to classify.</param>
        /// <param name="model">The semantic model of the call's syntax tree.</param>
        /// <param name="cancellationToken">Cancels the walk.</param>
        private static IrProviderKind? ProviderKind(IOperation value, SemanticModel model, CancellationToken cancellationToken)
        {
            switch (Unwrap(value))
            {
                case IPropertyReferenceOperation { Property: var property }:
                    return IsProperty(property, HTTP_ABSTRACTIONS, HTTP_CONTEXT, "RequestServices") ? IrProviderKind.RequestServices
                        : IsProperty(property, DiIndexBuilder.Hosting, HOST, "Services") ? IrProviderKind.HostServices
                        : IsProperty(property, HTTP_ABSTRACTIONS, APPLICATION_BUILDER, "ApplicationServices") ? IrProviderKind.ApplicationServices
                        : IsProperty(property, DiIndexBuilder.DependencyInjection, SERVICE_SCOPE, "ServiceProvider") ||
                          IsProperty(property, DiIndexBuilder.DependencyInjection, ASYNC_SERVICE_SCOPE, "ServiceProvider")
                            ? IrProviderKind.ScopeServiceProvider
                            : null;
                case IParameterReferenceOperation { Parameter: var parameter }:
                    return IsServiceProvider(parameter.Type) && IsConstructorParameter(parameter) ? IrProviderKind.InjectedProvider : null;
                case IFieldReferenceOperation { Instance: IInstanceReferenceOperation, Field: var field }:
                    return IsInjectedField(field, model.Compilation, cancellationToken) ? IrProviderKind.InjectedProvider : null;
                case ILocalReferenceOperation { Local: var local }:
                    return SingleInitializer(local, model, cancellationToken) is { } initializer
                        ? ProviderKind(initializer, model, cancellationToken)
                        : null;
                default:
                    return null;
            }
        }

        private static bool IsProperty(IPropertySymbol property, SupportedAssemblyVersion range, string type, string name)
        {
            for (IPropertySymbol? current = property; current is not null; current = current.OverriddenProperty)
            {
                if (current.Name == name && ExactSymbols.IsType(current.ContainingType, range, type))
                    return true;
            }

            var containing = property.ContainingType;
            return containing.AllInterfaces.Where(@interface => ExactSymbols.IsType(@interface, range, type))
                             .SelectMany(@interface => @interface.GetMembers(name).OfType<IPropertySymbol>())
                             .Any(member => SymbolEqualityComparer.Default.Equals(containing.FindImplementationForInterfaceMember(member), property));
        }

        private static bool IsConstructorParameter(IParameterSymbol parameter) =>
            parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor };

        /// <summary>An instance field of provider type whose every write is a constructor of its type storing one of the
        /// constructor's parameters, or an initializer storing a primary constructor parameter; at least one write.</summary>
        /// <param name="field">The field to test.</param>
        /// <param name="compilation">The compilation whose syntax trees declare the field's type.</param>
        /// <param name="cancellationToken">Cancels the walk.</param>
        private static bool IsInjectedField(IFieldSymbol field, Compilation compilation, CancellationToken cancellationToken)
        {
            if (field.IsStatic || !IsServiceProvider(field.Type))
                return false;

            var writes = 0;
            foreach (var declaration in field.ContainingType.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(cancellationToken)))
            {
                if (!compilation.ContainsSyntaxTree(declaration.SyntaxTree))
                    return false;
                var model = compilation.GetSemanticModel(declaration.SyntaxTree);
                foreach (var node in declaration.DescendantNodes())
                {
                    switch (node)
                    {
                        case VariableDeclaratorSyntax { Initializer: { } initializer } declarator
                            when SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(declarator, cancellationToken), field):
                            if (!IsConstructorParameterValue(model.GetOperation(initializer.Value, cancellationToken)))
                                return false;
                            writes++;
                            break;
                        case AssignmentExpressionSyntax assignment
                            when SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol, field):
                            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                                model.GetEnclosingSymbol(assignment.SpanStart, cancellationToken) is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor ||
                                !SymbolEqualityComparer.Default.Equals(constructor.ContainingType, field.ContainingType) ||
                                !IsConstructorParameterValue(model.GetOperation(assignment.Right, cancellationToken)))
                            {
                                return false;
                            }
                            writes++;
                            break;
                    }
                }
            }

            return writes != 0;
        }

        /// <summary>A constructor parameter, possibly converted or guarded by <c>?? throw</c>.</summary>
        /// <param name="value">The value written.</param>
        private static bool IsConstructorParameterValue(IOperation? value) => Unwrap(value) switch
        {
            IParameterReferenceOperation { Parameter: var parameter } => IsConstructorParameter(parameter),
            ICoalesceOperation { WhenNull: IThrowOperation } coalesce => IsConstructorParameterValue(coalesce.Value),
            _ => false
        };

        /// <summary>The initializer of a local declared once with one, when nothing else in its member writes it.</summary>
        /// <param name="local">The local to look up.</param>
        /// <param name="model">The semantic model of the syntax tree the local must be declared in.</param>
        /// <param name="cancellationToken">Cancels the walk.</param>
        private static IOperation? SingleInitializer(ILocalSymbol local, SemanticModel model, CancellationToken cancellationToken)
        {
            if (local.DeclaringSyntaxReferences is not [var reference] ||
                reference.GetSyntax(cancellationToken) is not VariableDeclaratorSyntax { Initializer: { } initializer } declarator ||
                declarator.SyntaxTree != model.SyntaxTree)
            {
                return null;
            }

            var member = declarator.Ancestors().FirstOrDefault(ancestor => ancestor is MemberDeclarationSyntax and not GlobalStatementSyntax) ??
                         declarator.SyntaxTree.GetRoot(cancellationToken);
            var written = member.DescendantNodes().OfType<IdentifierNameSyntax>()
                                .Where(identifier => identifier.Identifier.ValueText == local.Name && IsWrite(identifier))
                                .Any(identifier => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, local));
            return written ? null : model.GetOperation(initializer.Value, cancellationToken);
        }

        private static bool IsWrite(IdentifierNameSyntax identifier) => identifier.Parent switch
        {
            AssignmentExpressionSyntax assignment => assignment.Left == identifier,
            ArgumentSyntax argument => argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ||
                                       argument.Parent is TupleExpressionSyntax,
            _ => false
        };

        private static IOperation? Unwrap(IOperation? value)
        {
            while (value is IConversionOperation or IParenthesizedOperation)
                value = value is IConversionOperation conversion ? conversion.Operand : ((IParenthesizedOperation)value).Operand;
            return value;
        }

        private static bool IsServiceProvider(ITypeSymbol? type) =>
            type is INamedTypeSymbol && type.ToDisplayString() == SERVICE_PROVIDER;
    }
}
