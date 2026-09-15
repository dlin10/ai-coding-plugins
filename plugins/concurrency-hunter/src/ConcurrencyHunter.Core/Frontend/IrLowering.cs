using System.Globalization;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace ConcurrencyHunter.Frontend;

public sealed record IrLoweredMethod(IrBody Body, IReadOnlyList<IrBody> NestedBodies);

public static class IrLowering
{
    private static readonly System.Text.RegularExpressions.Regex NESTED_BODY_SUFFIX =
        new(@"(?:#lambda\d+|#local:[^#~]+(?:~\d+)?)+$");

    public static IrLoweredMethod Lower(IMethodSymbol method, Compilation compilation, string rootDirectory,
                                        CancellationToken cancellationToken)
    {
        var declarationReference = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (declarationReference is null)
            throw new ArgumentException("The method has no source body.", nameof(method));

        var declaration = declarationReference.GetSyntax(cancellationToken);
        var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);
        var graph = ControlFlowGraph.Create(declaration, semanticModel, cancellationToken);
        if (graph is null)
            throw new ArgumentException("The method has no source body.", nameof(method));

        var bodyId = RootBodyId(method);
        return LowerGraph(
            method,
            graph,
            bodyId,
            SymbolNames.Method(method),
            !method.IsStatic,
            new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default),
            rootDirectory,
            cancellationToken);
    }

    /// <summary>The IR body id of every lambda and local function nested in <paramref name="method"/>, at any depth,
    /// keyed by the span of the nested function's declaring syntax; the ids are those <see cref="Lower"/> assigns.</summary>
    public static IReadOnlyDictionary<(SyntaxTree Tree, Microsoft.CodeAnalysis.Text.TextSpan Span), string> NestedBodyIds(
        IMethodSymbol method, Compilation compilation, CancellationToken cancellationToken)
    {
        var declaration = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken)
                          ?? throw new ArgumentException("The method has no source body.", nameof(method));
        var graph = ControlFlowGraph.Create(declaration, compilation.GetSemanticModel(declaration.SyntaxTree), cancellationToken)
                    ?? throw new ArgumentException("The method has no source body.", nameof(method));
        var ids = new Dictionary<(SyntaxTree, Microsoft.CodeAnalysis.Text.TextSpan), string>();
        CollectNestedBodyIds(graph, RootBodyId(method), new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default),
                             ids, cancellationToken);
        return ids;
    }

    private static void CollectNestedBodyIds(ControlFlowGraph graph, string bodyId, IReadOnlyDictionary<IMethodSymbol, string> outerIds,
                                             Dictionary<(SyntaxTree, Microsoft.CodeAnalysis.Text.TextSpan), string> ids,
                                             CancellationToken cancellationToken)
    {
        var (localFunctions, anonymousFunctions, nestedIds) = AssignNestedIds(graph, bodyId, outerIds);
        foreach (var localFunction in localFunctions)
        {
            if (localFunction.DeclaringSyntaxReferences.FirstOrDefault() is { } reference)
                ids[(reference.SyntaxTree, reference.Span)] = nestedIds[localFunction];
            CollectNestedBodyIds(graph.GetLocalFunctionControlFlowGraph(localFunction, cancellationToken), nestedIds[localFunction],
                                 nestedIds, ids, cancellationToken);
        }

        foreach (var anonymousFunction in anonymousFunctions)
        {
            ids[(anonymousFunction.Syntax.SyntaxTree, anonymousFunction.Syntax.Span)] = nestedIds[anonymousFunction.Symbol];
            CollectNestedBodyIds(graph.GetAnonymousFunctionControlFlowGraph(anonymousFunction, cancellationToken),
                                 nestedIds[anonymousFunction.Symbol], nestedIds, ids, cancellationToken);
        }
    }

    private static (IMethodSymbol[] LocalFunctions, IFlowAnonymousFunctionOperation[] AnonymousFunctions,
                    Dictionary<IMethodSymbol, string> NestedIds) AssignNestedIds(
        ControlFlowGraph graph, string bodyId, IReadOnlyDictionary<IMethodSymbol, string> outerIds)
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
            nestedIds.Add(anonymousFunctions[index].Symbol, $"{bodyId}#lambda{index + 1}");
        return (localFunctions, anonymousFunctions, nestedIds);
    }

    private static IrLoweredMethod LowerGraph(IMethodSymbol method, ControlFlowGraph graph, string bodyId,
                                              string ownerSymbol, bool hasReceiver,
                                              IReadOnlyDictionary<IMethodSymbol, string> outerIds,
                                              string rootDirectory, CancellationToken cancellationToken)
    {
        var (localFunctions, anonymousFunctions, nestedIds) = AssignNestedIds(graph, bodyId, outerIds);

        var body = new BodyLowerer(
            method,
            graph,
            bodyId,
            ownerSymbol,
            hasReceiver,
            nestedIds,
            rootDirectory,
            cancellationToken).Lower();
        var nestedBodies = new List<IrBody>();
        foreach (var localFunction in localFunctions)
        {
            var nested = LowerGraph(
                localFunction,
                graph.GetLocalFunctionControlFlowGraph(localFunction, cancellationToken),
                nestedIds[localFunction],
                ownerSymbol,
                hasReceiver,
                nestedIds,
                rootDirectory,
                cancellationToken);
            nestedBodies.Add(nested.Body);
            nestedBodies.AddRange(nested.NestedBodies);
        }

        foreach (var anonymousFunction in anonymousFunctions)
        {
            var nested = LowerGraph(
                anonymousFunction.Symbol,
                graph.GetAnonymousFunctionControlFlowGraph(anonymousFunction, cancellationToken),
                nestedIds[anonymousFunction.Symbol],
                ownerSymbol,
                hasReceiver,
                nestedIds,
                rootDirectory,
                cancellationToken);
            nestedBodies.Add(nested.Body);
            nestedBodies.AddRange(nested.NestedBodies);
        }

        return new IrLoweredMethod(body, nestedBodies);
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

    private sealed class BodyLowerer
    {
        private readonly IMethodSymbol _method;
        private readonly ControlFlowGraph _graph;
        private readonly string _bodyId;
        private readonly string _ownerSymbol;
        private readonly bool _hasReceiver;
        private readonly IReadOnlyDictionary<IMethodSymbol, string> _nestedIds;
        private readonly string _rootDirectory;
        private readonly CancellationToken _cancellationToken;
        private readonly EffectiveFlowGraph _flowGraph;
        private readonly SsaPlan _ssaPlan;
        private readonly List<IrValue> _values = [];
        private readonly List<IrRegion> _regions = [];
        private readonly Dictionary<ControlFlowRegion, int> _regionIds = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ISymbol, int> _symbolValues = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<SsaVariable, int> _ssaVersions = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<CaptureId, int> _captureValues = [];
        private readonly Dictionary<SsaToken, int> _tokenValues = [];
        private readonly Dictionary<SsaVariable, int> _definitionPositions = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(SyntaxTree Tree, int Start, int Length), int> _coalesceLoads = [];
        private readonly Dictionary<CaptureId, IOperation> _capturedTargets = [];
        private List<IrOperation> _operations = [];
        private int _currentBlockOrdinal;
        private int? _receiverValue;
        private int _nextValueId;
        private int _nextOperationId;

        internal BodyLowerer(IMethodSymbol method, ControlFlowGraph graph, string bodyId, string ownerSymbol,
                             bool hasReceiver, IReadOnlyDictionary<IMethodSymbol, string> nestedIds,
                             string rootDirectory, CancellationToken cancellationToken)
        {
            _method = method;
            _graph = graph;
            _bodyId = bodyId;
            _ownerSymbol = ownerSymbol;
            _hasReceiver = hasReceiver;
            _nestedIds = nestedIds;
            _rootDirectory = Path.GetFullPath(rootDirectory);
            _cancellationToken = cancellationToken;
            _flowGraph = EffectiveFlowGraph.Create(graph);
            _ssaPlan = SsaPlan.Create(method, graph, _flowGraph);
        }

        internal IrBody Lower()
        {
            AddInitialValues();
            AllocateSsaValues();
            AddRegion(_graph.Root, null);
            var blocks = _graph.Blocks.Select(LowerBlock).ToArray();
            var body = new IrBody(
                _bodyId,
                BodyKind(_method),
                _ownerSymbol,
                SymbolNames.Method(_method),
                _values,
                blocks,
                _regions);
            var problems = IrValidator.Validate(body);
            if (problems.Count != 0)
                throw new InvalidOperationException("Lowered IR is invalid: " + string.Join("; ", problems));
            return body;
        }

        private void AddInitialValues()
        {
            if (_hasReceiver)
                _receiverValue = AddValue(IrValueKind.Receiver, _method.ContainingType, "this", 0);

            foreach (var variable in _ssaPlan.Variables.Where(variable => variable.IsIncoming))
            {
                var value = AddValue(variable.Kind, variable.Type, variable.Name, 0);
                _tokenValues.Add(variable.Incoming, value);
                _ssaVersions.Add(variable, 0);
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
                region.FirstBlockOrdinal,
                region.LastBlockOrdinal,
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
                        new IrFlowPredecessor(input.Edge.Source, input.Edge.Kind),
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
                        Provenance(variable.FirstReference ?? _graph.OriginalOperation, "capture")));
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

            var predecessors = block.Predecessors.Select(predecessor => predecessor.Source.Ordinal).ToArray();
            var flowPredecessors = _flowGraph.Predecessors(block.Ordinal)
                                                   .Select(edge => new IrFlowPredecessor(edge.Source, edge.Kind))
                                                   .ToArray();
            return new IrBlock(
                block.Ordinal,
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
                branch.Destination?.Ordinal,
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
            var arguments = property.Arguments.Select(LowerArgument).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            return AddCall(property, getter, receiver, arguments, property.Type);
        }

        private int LowerPropertyStore(IPropertyReferenceOperation property, int value, IOperation source,
                                       string transformation)
        {
            var setter = property.Property.SetMethod;
            if (setter is null)
                return Unknown(source, "unsupported");
            int? receiver = property.Instance is null ? null : LowerValue(property.Instance);
            var arguments = property.Arguments.Select(LowerArgument)
                                               .Where(argument => argument.HasValue)
                                               .Select(argument => argument!.Value)
                                               .Append(value)
                                               .ToArray();
            _operations.Add(new IrCallOperation(
                NextOperation(), null, CallKind(setter), SymbolNames.Method(setter), receiver, arguments,
                Provenance(source, transformation)));
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
            var arguments = invocation.Arguments.Select(LowerArgument)
                                                 .Where(argument => argument.HasValue)
                                                 .Select(argument => argument!.Value)
                                                 .ToArray();
            return AddCall(invocation, invocation.TargetMethod, receiver, arguments, invocation.Type);
        }

        private int AddCall(IOperation source, IMethodSymbol method, int? receiver,
                            IReadOnlyList<int> arguments, ITypeSymbol? resultType)
        {
            int? result = resultType is null || resultType.SpecialType == SpecialType.System_Void
                ? null
                : AddTemporary(resultType);
            var methodName = _nestedIds.GetValueOrDefault(method.OriginalDefinition) ?? SymbolNames.Method(method);
            _operations.Add(new IrCallOperation(
                NextOperation(), result, CallKind(method), methodName, receiver, arguments,
                Provenance(source, "call")));
            return result ?? Constant(source, null, "void");
        }

        private bool TryLowerMonitor(IInvocationOperation invocation, out int result)
        {
            var method = invocation.TargetMethod;
            var isMonitor = method.IsStatic &&
                            method.ContainingType.ToDisplayString() == "System.Threading.Monitor";
            var isEnter = isMonitor && method.Name == "Enter" &&
                          (method.Parameters.Length == 1 ||
                           method.Parameters is [{ Type.SpecialType: SpecialType.System_Object },
                               { Type.SpecialType: SpecialType.System_Boolean, RefKind: RefKind.Ref }]);
            var isExit = isMonitor && method.Name == "Exit" && method.Parameters.Length == 1;
            if (!isEnter && !isExit)
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
            _operations.Add(new IrAllocateOperation(
                NextOperation(), result, TypeName(creation.Type), provenance));
            if (creation.Constructor is not null)
            {
                var arguments = creation.Arguments.Select(LowerArgument)
                                                  .Where(argument => argument.HasValue)
                                                  .Select(argument => argument!.Value)
                                                  .ToArray();
                _operations.Add(new IrCallOperation(
                    NextOperation(), null, IrCallKind.Constructor, SymbolNames.Method(creation.Constructor),
                    result, arguments, provenance));
            }
            return result;
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
            int? receiver = null;
            switch (delegateCreation.Target)
            {
                case IFlowAnonymousFunctionOperation anonymousFunction:
                    targetBodyId = _nestedIds.GetValueOrDefault(anonymousFunction.Symbol);
                    break;
                case IMethodReferenceOperation methodReference:
                    if (_nestedIds.TryGetValue(methodReference.Method.OriginalDefinition, out var localBodyId))
                        targetBodyId = localBodyId;
                    else
                        targetMethod = SymbolNames.Method(methodReference.Method);
                    receiver = methodReference.Instance is null ? null : LowerValue(methodReference.Instance);
                    break;
            }

            _operations.Add(new IrCreateDelegateOperation(
                NextOperation(), result, targetBodyId, targetMethod, receiver,
                Provenance(delegateCreation, "delegate-creation")));
            return result;
        }

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

        private FieldLocation GetPropertyLocation(IPropertyReferenceOperation reference)
        {
            var property = reference.Property;
            return new FieldLocation(
                reference.Instance is null ? null : LowerValue(reference.Instance),
                new IrFieldRef(
                    property.ContainingAssembly.Name,
                    SymbolNames.Type(property.ContainingType),
                    property.Name,
                    IrFieldKind.PropertyBackingField,
                    property.IsStatic,
                    property.SetMethod is null,
                    SymbolNames.Type(property.Type),
                    SymbolNames.TypeIdentity(property.ContainingType)));
        }

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

        private int AddSsaValue(SsaVariable variable)
        {
            var version = _ssaVersions.GetValueOrDefault(variable) + 1;
            _ssaVersions[variable] = version;
            return AddValue(variable.Kind, variable.Type, variable.Name, version);
        }

        private int AddTemporary(ITypeSymbol? type) =>
            AddValue(IrValueKind.Temporary, type, $"t{_nextValueId}", 0);

        private int AddValue(IrValueKind kind, ITypeSymbol? type, string name, int version)
        {
            var id = _nextValueId++;
            _values.Add(new IrValue(id, kind, TypeName(type), name, version));
            return id;
        }

        private int NextOperation() => _nextOperationId++;

        private IrProvenance Provenance(IOperation operation, string transformation)
        {
            var lineSpan = operation.Syntax.GetLocation().GetLineSpan();
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
            return new IrProvenance(span, SymbolNames.Method(_method), operation.Syntax.Kind().ToString(), transformation);
        }

        private static IrBodyKind BodyKind(IMethodSymbol method) => method.MethodKind switch
        {
            MethodKind.AnonymousFunction => IrBodyKind.Lambda,
            MethodKind.LocalFunction => IrBodyKind.LocalFunction,
            _ => IrBodyKind.Method
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

        private readonly record struct FieldLocation(int? Receiver, IrFieldRef Field);
    }
}
