using System.Globalization;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace ConcurrencyHunter.Frontend;

public sealed record IrLoweredMethod(IrBody Body, IReadOnlyList<IrBody> NestedBodies);

public static class IrLowering
{
    private const string THIS_KEY = "this";

    private static readonly System.Text.RegularExpressions.Regex NESTED_BODY_SUFFIX =
        new(@"(?:#lambda\d+|#local:[^#~]+(?:~\d+)?)+$");

    /// <summary>Lowers a member with a source body. A constructor's body runs its type's initializers (unless it chains to
    /// <c>this(...)</c>), then the base or <c>this</c> call, then its own body; a type initializer runs the static initializers,
    /// then the static constructor body; an auto-property accessor loads or stores its backing field.</summary>
    public static IrLoweredMethod Lower(IMethodSymbol method, Compilation compilation, string rootDirectory,
                                        CancellationToken cancellationToken)
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
                                          cancellationToken);
        var ownerSymbol = SymbolNames.Method(method);
        var body = new BodyLowerer(method, plan.Segments, plan.BodyId, ownerSymbol, !method.IsStatic, nestedIds, context).Lower();
        var nestedBodies = new List<IrBody>();
        foreach (var (graph, localFunctions, anonymousFunctions) in functions)
            nestedBodies.AddRange(LowerNestedFunctions(graph, localFunctions, anonymousFunctions, ownerSymbol, !method.IsStatic, nestedIds, context));
        return new IrLoweredMethod(body, nestedBodies);
    }

    /// <summary>The IR body id of every lambda and local function nested in <paramref name="method"/>, at any depth,
    /// keyed by the span of the nested function's declaring syntax; the ids are those <see cref="Lower"/> assigns.</summary>
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
    public static string EnclosingMethodBodyId(string bodyId) => NESTED_BODY_SUFFIX.Replace(bodyId, "");

    internal static string RootBodyId(IMethodSymbol method) =>
        $"body:{method.ContainingAssembly.Name}:{method.GetDocumentationCommentId() ?? method.ToDisplayString()}";

    internal static bool IsMonitorEnterOrExit(IMethodSymbol method) => IsMonitorEnter(method) || IsMonitorExit(method);

    private static bool IsMonitorEnter(IMethodSymbol method) =>
        IsMonitor(method) && method.Name == "Enter" &&
        (method.Parameters.Length == 1 ||
         method.Parameters is [{ Type.SpecialType: SpecialType.System_Object },
             { Type.SpecialType: SpecialType.System_Boolean, RefKind: RefKind.Ref }]);

    private static bool IsMonitorExit(IMethodSymbol method) => IsMonitor(method) && method.Name == "Exit" && method.Parameters.Length == 1;

    private static bool IsMonitor(IMethodSymbol method) =>
        method.IsStatic && method.ContainingType?.ToDisplayString() == "System.Threading.Monitor";

    /// <summary>A source property whose accessors all lack bodies and which is neither abstract, extern nor an interface
    /// member: its accessors have synthesized bodies over its backing field.</summary>
    internal static bool IsAutoProperty(IPropertySymbol property, CancellationToken cancellationToken) =>
        !property.IsAbstract && !property.IsExtern && property.ContainingType.TypeKind != TypeKind.Interface &&
        property.DeclaringSyntaxReferences.Length != 0 &&
        property.DeclaringSyntaxReferences.All(reference =>
            reference.GetSyntax(cancellationToken) is PropertyDeclarationSyntax { AccessorList: { Accessors.Count: > 0 } accessors } &&
            accessors.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null));

    /// <summary>The field and property initializers of <paramref name="type"/> with the member each initializes, instance or
    /// static, ordered by file path (ordinal) and span start.</summary>
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
    private static IReadOnlyList<IParameterSymbol> CapturedPrimaryConstructorParameters(INamedTypeSymbol type, IMethodSymbol constructor,
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

    /// <summary>Whether a reference sits in a field or property initializer and not in a lambda or local function that initializer
    /// declares; the constructor runs such a reference itself, so it is not a capture.</summary>
    private static bool IsDirectlyInInitializer(SyntaxNode reference)
    {
        foreach (var ancestor in reference.Ancestors())
        {
            if (ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                return false;
            if (ancestor is EqualsValueClauseSyntax
                {
                    Parent: VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax } or PropertyDeclarationSyntax
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

    private sealed record LoweringContext(string RootBodyId, SiteOrdinals SiteOrdinals, string RootDirectory,
                                          CancellationToken CancellationToken);

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
        private readonly Dictionary<IParameterSymbol, int> _parameterValues = new(SymbolEqualityComparer.Default);
        private ControlFlowGraph _graph = null!;
        private EffectiveFlowGraph _flowGraph = null!;
        private SsaPlan _ssaPlan = null!;
        private int _offset;
        private List<IrOperation> _operations = [];
        private int _currentBlockOrdinal;
        private int? _receiverValue;
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
            var blocks = _segments is [GraphSegment only] ? LowerSingleGraph(only) : LowerSegments();
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
                                    .ToArray()
            };
            var problems = IrValidator.Validate(body);
            if (problems.Count != 0)
                throw new InvalidOperationException("Lowered IR is invalid: " + string.Join("; ", problems));
            return body;
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
                _operations.Add(new IrPhiOperation(
                    NextOperation(), ResolveToken(token), inputs,
                    Provenance(block.Operations.FirstOrDefault() ?? block.BranchValue ?? _graph.OriginalOperation, "phi")));
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

            foreach (var operation in block.Operations)
                LowerTop(operation);

            int? branchValue = block.BranchValue is null ? null : LowerValue(block.BranchValue);
            if (block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
            {
                _operations.Add(new IrReturnOperation(
                    NextOperation(),
                    branchValue,
                    Provenance(block.BranchValue ?? block.Operations.LastOrDefault() ?? _graph.OriginalOperation, "return")));
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
                    StoreSymbol(declarator.Symbol, LowerValue(declarator.Initializer.Value), declarator, "declaration");
                    break;
                case IVariableDeclaratorOperation declarator:
                    GetSymbolValue(declarator.Symbol);
                    break;
                case IDeconstructionAssignmentOperation deconstruction:
                    LowerDeconstruction(deconstruction);
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
                IFieldReferenceOperation field => LowerFieldLoad(field, "direct"),
                IPropertyReferenceOperation property => LowerPropertyLoad(property),
                IParameterReferenceOperation parameter => LowerParameter(parameter),
                ILocalReferenceOperation local => GetSymbolValue(local.Local),
                IInstanceReferenceOperation => _receiverValue ?? Unknown(operation, "unsupported"),
                IFlowCaptureOperation capture => LowerCapture(capture),
                IFlowCaptureReferenceOperation capture => LowerCaptureReference(capture),
                IInvocationOperation invocation => LowerInvocation(invocation),
                IObjectCreationOperation creation => LowerObjectCreation(creation),
                IArrayCreationOperation creation => LowerArrayCreation(creation),
                IAwaitOperation awaitOperation => LowerAwait(awaitOperation),
                IArrayElementReferenceOperation element => LowerElementLoad(element),
                IIsNullOperation isNull => LowerNullTest(isNull, isNull.Operand, "null-test"),
                IBinaryOperation binary when NullTestOperand(binary) is { } tested => LowerNullTest(binary, tested, "binary"),
                IBinaryOperation binary => LowerBinary(binary),
                IUnaryOperation unary => LowerUnary(unary),
                IIsTypeOperation typeTest => LowerTypeTest(typeTest),
                IIsPatternOperation pattern when IsNullPattern(pattern.Pattern) => LowerNullTest(pattern, pattern.Value, "is-pattern"),
                IDelegateCreationOperation delegateCreation => LowerDelegateCreation(delegateCreation),
                IArgumentOperation argument => LowerArgument(argument) ?? Unknown(argument, "address-taken"),
                _ when operation.ConstantValue.HasValue => Constant(operation, "constant"),
                _ => LowerUnsupported(operation)
            };
        }

        private int LowerAssignment(ISimpleAssignmentOperation assignment)
        {
            var value = LowerValue(assignment.Value);
            return LowerStore(assignment.Target, value, assignment, "assignment");
        }

        private int LowerCompoundAssignment(ICompoundAssignmentOperation compound)
        {
            if (TryLocation(compound.Target, out var location))
            {
                var loaded = AddTemporary(compound.Target.Type);
                var loadId = NextOperation();
                var provenance = Provenance(compound, "compound-assignment");
                _operations.Add(new IrLoadFieldOperation(
                    loadId, loaded, location.Receiver, location.Field, provenance));
                var right = LowerValue(compound.Value);
                var result = AddTemporary(compound.Type);
                _operations.Add(new IrComputeOperation(
                    NextOperation(), result, compound.OperatorKind.ToString(), [loaded, right], provenance));
                _operations.Add(new IrStoreFieldOperation(
                    NextOperation(), location.Receiver, location.Field, result, loadId, provenance));
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
                var result = AddTemporary(compound.Type);
                _operations.Add(new IrComputeOperation(
                    NextOperation(), result, compound.OperatorKind.ToString(), [left, right],
                    Provenance(compound, "compound-assignment")));
                StoreSymbol(symbol, result, compound, "compound-assignment");
                return result;
            }

            return Unknown(compound, "unsupported");
        }

        private int LowerIncrement(IIncrementOrDecrementOperation increment)
        {
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
                if (symbol is null)
                    return Unknown(increment, "unsupported");

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
            var one = Constant(increment, 1, "increment");
            var result = AddTemporary(increment.Type);
            var @operator = increment.Kind == OperationKind.Decrement ? "Subtract" : "Add";
            _operations.Add(new IrComputeOperation(NextOperation(), result, @operator, [loaded, one], provenance));
            _operations.Add(new IrStoreFieldOperation(
                NextOperation(), location.Receiver, location.Field, result, loadId, provenance));
            return increment.IsPostfix ? loaded : result;
        }

        private int LowerDeconstruction(IDeconstructionAssignmentOperation deconstruction)
        {
            var stores = new List<(IOperation Target, int Value)>();
            CollectDeconstruction(deconstruction.Target, deconstruction.Value, null, deconstruction, stores);
            foreach (var (target, value) in stores)
                LowerStore(target, value, deconstruction, "deconstruction");
            if (stores.Count == 0)
                return Unknown(deconstruction, "unsupported");
            return stores[^1].Value;
        }

        // Pairs every target leaf with its value, lowering the right-hand side left to right before any store: a tuple
        // literal element by element, and any other tuple value once, then one element per target.
        private void CollectDeconstruction(IOperation target, IOperation? source, int? sourceValue, IOperation deconstruction,
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
                    CollectDeconstruction(targetTuple.Elements[index], sourceTuple.Elements[index], null, deconstruction, stores);
                return;
            }

            var tupleValue = sourceValue ?? LowerValue(source!);
            for (var index = 0; index < targetTuple.Elements.Length; index++)
            {
                var element = targetTuple.Elements[index];
                var elementValue = AddTemporary(element.Type);
                _operations.Add(new IrComputeOperation(
                    NextOperation(), elementValue, $"tuple-element:Item{index + 1}", [tupleValue],
                    Provenance(deconstruction, "deconstruction")));
                CollectDeconstruction(element, null, elementValue, deconstruction, stores);
            }
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
                case IPropertyReferenceOperation property:
                    return LowerPropertyStore(property, value, source, transformation);
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
                    return StoreSymbol(local.Local, value, source, transformation);
                case IParameterReferenceOperation parameter:
                    if (!IsPrimaryConstructorParameter(parameter.Parameter))
                        return StoreSymbol(parameter.Parameter, value, source, transformation);
                    var primaryLocation = GetPrimaryConstructorParameterLocation(parameter.Parameter);
                    _operations.Add(new IrStoreFieldOperation(
                        NextOperation(), primaryLocation.Receiver, primaryLocation.Field, value, null,
                        Provenance(source, transformation)));
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
            return targetValue;
        }

        private int LowerFieldLoad(IFieldReferenceOperation field, string transformation)
        {
            var location = GetFieldLocation(field);
            var result = AddTemporary(field.Type);
            var operationId = NextOperation();
            _operations.Add(new IrLoadFieldOperation(
                operationId, result, location.Receiver, location.Field, Provenance(field, transformation)));
            RememberCoalesceLoad(field, operationId);
            return result;
        }

        private int LowerPropertyLoad(IPropertyReferenceOperation property)
        {
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

            var getter = property.Property.GetMethod;
            if (getter is null)
                return Unknown(property, "unsupported");
            int? receiver = property.Instance is null ? null : LowerValue(property.Instance);
            return AddCall(property, getter, receiver, LowerArguments(property.Arguments), property.Type);
        }

        private int LowerPropertyStore(IPropertyReferenceOperation property, int value, IOperation source,
                                       string transformation)
        {
            var setter = property.Property.SetMethod;
            if (setter is null)
                return Unknown(source, "unsupported");
            int? receiver = property.Instance is null ? null : LowerValue(property.Instance);
            var arguments = LowerArguments(property.Arguments);
            arguments = arguments with
            {
                Values = [.. arguments.Values, value],
                Ordinals = [.. arguments.Ordinals, setter.Parameters.Length - 1]
            };
            _operations.Add(Call(null, setter, receiver, arguments, Provenance(source, transformation)));
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
            new(
                _receiverValue,
                new IrFieldRef(
                    parameter.ContainingType.ContainingAssembly.Name,
                    SymbolNames.Type(parameter.ContainingType),
                    parameter.Name,
                    IrFieldKind.PrimaryConstructorParameter,
                    false,
                    false,
                    SymbolNames.Type(parameter.Type),
                    SymbolNames.TypeIdentity(parameter.ContainingType)));

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
            var sourceValue = LowerValue(capture.Value);
            var variable = _ssaPlan.GetVariable(capture.Id);
            var targetValue = ResolveToken(NextDefinition(variable));
            SetCurrent(variable, targetValue);
            _operations.Add(new IrAssignOperation(
                NextOperation(), targetValue, sourceValue, Provenance(capture, "flow-capture")));
            return targetValue;
        }

        private int LowerCaptureReference(IFlowCaptureReferenceOperation capture)
        {
            return _captureValues.TryGetValue(capture.Id, out var value)
                ? value
                : Unknown(capture, "unsupported");
        }

        private int LowerInvocation(IInvocationOperation invocation)
        {
            if (TryLowerMonitor(invocation, out var monitorResult))
                return monitorResult;

            int? receiver = invocation.Instance is null ? null : LowerValue(invocation.Instance);
            return AddCall(invocation, invocation.TargetMethod, receiver, LowerArguments(invocation.Arguments), invocation.Type);
        }

        private int AddCall(IOperation source, IMethodSymbol method, int? receiver,
                            LoweredArguments arguments, ITypeSymbol? resultType)
        {
            int? result = resultType is null || resultType.SpecialType == SpecialType.System_Void
                ? null
                : AddTemporary(resultType);
            _operations.Add(Call(result, method, receiver, arguments, Provenance(source, "call")));
            return result ?? Constant(source, null, "void");
        }

        private IrCallOperation Call(int? result, IMethodSymbol method, int? receiver, LoweredArguments arguments,
                                     IrProvenance provenance)
        {
            var nestedId = _nestedIds.GetValueOrDefault(method.OriginalDefinition);
            return new IrCallOperation(
                NextOperation(), result, CallKind(method), nestedId ?? SymbolNames.Method(method), receiver, arguments.Values,
                provenance)
            {
                ArgumentParameterOrdinals = arguments.Ordinals,
                RefResults = arguments.RefResults,
                TargetMethodId = nestedId is null ? RootBodyId(method.OriginalDefinition) : null,
                TargetContainingTypeKey = nestedId is null ? SymbolNames.TypeKey(method.ContainingType) : null,
                TargetMethodTypeArgumentKeys = nestedId is null ? method.TypeArguments.Select(SymbolNames.TypeKey).ToArray() : []
            };
        }

        private bool TryLowerMonitor(IInvocationOperation invocation, out int result)
        {
            var method = invocation.TargetMethod;
            var isEnter = IsMonitorEnter(method);
            if (!isEnter && !IsMonitorExit(method))
            {
                result = 0;
                return false;
            }

            var lockValue = LowerValue(invocation.Arguments[0].Value);
            foreach (var argument in invocation.Arguments.Skip(1))
                LowerArgument(argument);
            var transformation = invocation.IsImplicit && invocation.Syntax.AncestorsAndSelf().Any(syntax => syntax is LockStatementSyntax)
                ? "lock-statement"
                : "monitor-call";
            var provenance = Provenance(invocation, transformation);
            if (isEnter)
            {
                _operations.Add(new IrAcquireOperation(
                    NextOperation(), lockValue, IrSynchronizationPrimitive.Monitor,
                    IrLockMode.Exclusive, provenance));
            }
            else
            {
                _operations.Add(new IrReleaseOperation(
                    NextOperation(), lockValue, IrSynchronizationPrimitive.Monitor,
                    IrLockMode.Exclusive, provenance));
            }
            result = Constant(invocation, null, "void");
            return true;
        }

        /// <summary>Lowers the arguments in evaluation order, then defines a new version of every local or parameter passed by
        /// <c>ref</c> or <c>out</c>, keyed by the parameter it binds.</summary>
        private LoweredArguments LowerArguments(IEnumerable<IArgumentOperation> arguments)
        {
            var values = new List<int>();
            var ordinals = new List<int>();
            var passedByReference = new List<(int Ordinal, ISymbol Symbol)>();
            foreach (var argument in arguments)
            {
                var ordinal = argument.Parameter?.Ordinal ?? -1;
                if (LowerArgument(argument) is int value)
                {
                    values.Add(value);
                    ordinals.Add(ordinal);
                }

                if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out && RefArgumentSymbol(argument) is { } symbol)
                    passedByReference.Add((ordinal, symbol));
            }

            var refResults = new Dictionary<int, int>();
            foreach (var (ordinal, symbol) in passedByReference)
            {
                if (!_ssaPlan.TryGetVariable(symbol, out var variable))
                    throw new InvalidOperationException($"No SSA variable was planned for '{symbol.Name}'.");
                var defined = ResolveToken(NextDefinition(variable));
                SetCurrent(variable, defined);
                refResults[ordinal] = defined;
            }

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

            var value = UnwrapTarget(argument.Value);
            IReadOnlyList<int> operands = value switch
            {
                ILocalReferenceOperation local => [GetSymbolValue(local.Local)],
                IParameterReferenceOperation parameter when IsPrimaryConstructorParameter(parameter.Parameter) => [],
                IParameterReferenceOperation parameter => [GetSymbolValue(parameter.Parameter)],
                _ => []
            };
            _operations.Add(new IrUnknownOperation(
                NextOperation(), null, argument.Value.Kind.ToString(), "address-taken", operands,
                Provenance(argument, "address-taken")));
            return operands.Count == 0 ? null : operands[0];
        }

        private int LowerObjectCreation(IObjectCreationOperation creation)
        {
            var result = AddTemporary(creation.Type);
            var provenance = Provenance(creation, "object-creation");
            _operations.Add(new IrAllocateOperation(NextOperation(), result, TypeName(creation.Type), provenance)
            {
                AllocatedTypeKey = TypeKeyOf(creation.Type),
                SiteOrdinal = _context.SiteOrdinals.Of(creation)
            });
            if (creation.Constructor is not null)
                _operations.Add(Call(null, creation.Constructor, result, LowerArguments(creation.Arguments), provenance));
            return result;
        }

        /// <summary>An array creation evaluates its sizes, allocates the array and stores each initializer element in order.</summary>
        private int LowerArrayCreation(IArrayCreationOperation creation)
        {
            foreach (var size in creation.DimensionSizes)
                LowerValue(size);
            var result = AddTemporary(creation.Type);
            _operations.Add(new IrAllocateOperation(NextOperation(), result, TypeName(creation.Type), Provenance(creation, "array-creation"))
            {
                AllocatedTypeKey = TypeKeyOf(creation.Type),
                SiteOrdinal = _context.SiteOrdinals.Of(creation)
            });
            if (creation.Initializer is not null)
                StoreElements(result, creation.Initializer, [], creation.DimensionSizes[0].Type);
            return result;
        }

        private void StoreElements(int array, IArrayInitializerOperation initializer, IReadOnlyList<int> outerIndices,
                                   ITypeSymbol? indexType)
        {
            for (var position = 0; position < initializer.ElementValues.Length; position++)
            {
                var element = initializer.ElementValues[position];
                var index = AddValue(IrValueKind.Constant, indexType, position.ToString(CultureInfo.InvariantCulture), 0);
                if (element is IArrayInitializerOperation nested)
                {
                    StoreElements(array, nested, [.. outerIndices, index], indexType);
                    continue;
                }

                var value = LowerValue(element);
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
                NextOperation(), result, awaitable, Provenance(awaitOperation, "await")));
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
            var result = AddTemporary(binary.Type);
            if (binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
            {
                _operations.Add(new IrCompareOperation(
                    NextOperation(), result, IrComparisonKind.Equality, left, right, null,
                    Provenance(binary, "binary")));
            }
            else if (binary.OperatorKind is BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or
                     BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual)
            {
                _operations.Add(new IrCompareOperation(
                    NextOperation(), result, IrComparisonKind.Ordering, left, right, null,
                    Provenance(binary, "binary")));
            }
            else
            {
                _operations.Add(new IrComputeOperation(
                    NextOperation(), result, binary.OperatorKind.ToString(), [left, right],
                    Provenance(binary, "binary")));
            }
            return result;
        }

        /// <summary>A null test of <paramref name="tested"/>. A negated test has the same compare, as <c>!=</c> has today.</summary>
        private int LowerNullTest(IOperation test, IOperation tested, string transformation)
        {
            var value = LowerValue(tested);
            var result = AddTemporary(test.Type);
            _operations.Add(new IrCompareOperation(
                NextOperation(), result, IrComparisonKind.Null, value, null, null, Provenance(test, transformation)));
            return result;
        }

        /// <summary>The operand <c>==</c> or <c>!=</c> compares with the <c>null</c> constant, when the operand is a reference
        /// or nullable value type and no user-defined operator is involved.</summary>
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
                SymbolNames.Type(typeTest.TypeOperand), Provenance(typeTest, "type-test")));
            return result;
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
                TargetMethodTypeArgumentKeys = target is null ? [] : target.TypeArguments.Select(SymbolNames.TypeKey).ToArray()
            });
            return result;
        }

        /// <summary>The keys of the variables the body of <paramref name="function"/> captures, in the order its capture
        /// operations name them.</summary>
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

        private int Unknown(IOperation operation, string reason, IReadOnlyList<int> operands)
        {
            int? result = operation.Type is null || operation.Type.SpecialType == SpecialType.System_Void
                ? null
                : AddTemporary(operation.Type);
            _operations.Add(new IrUnknownOperation(
                NextOperation(), result, operation.Kind.ToString(), reason, operands, Provenance(operation, reason)));
            return result ?? Constant(operation, null, reason);
        }

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

        private FieldLocation GetFieldLocation(IFieldReferenceOperation reference)
        {
            var field = reference.Field;
            return new FieldLocation(
                reference.Instance is null ? null : LowerValue(reference.Instance),
                new IrFieldRef(
                    field.ContainingAssembly.Name,
                    SymbolNames.Type(field.ContainingType),
                    field.Name,
                    IrFieldKind.Field,
                    field.IsStatic,
                    field.IsReadOnly,
                    SymbolNames.Type(field.Type),
                    SymbolNames.TypeIdentity(field.ContainingType)));
        }

        private FieldLocation GetPropertyLocation(IPropertyReferenceOperation reference) =>
            new(reference.Instance is null ? null : LowerValue(reference.Instance), PropertyField(reference.Property));

        private static IrFieldRef PropertyField(IPropertySymbol property) =>
            new(
                property.ContainingAssembly.Name,
                SymbolNames.Type(property.ContainingType),
                property.Name,
                IrFieldKind.PropertyBackingField,
                property.IsStatic,
                property.SetMethod is null,
                SymbolNames.Type(property.Type),
                SymbolNames.TypeIdentity(property.ContainingType));

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

        private sealed record LoweredArguments(IReadOnlyList<int> Values, IReadOnlyList<int> Ordinals,
                                               IReadOnlyDictionary<int, int> RefResults)
        {
            internal static LoweredArguments None { get; } = new([], [], new Dictionary<int, int>());
        }
    }
}
