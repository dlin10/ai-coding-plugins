using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace ConcurrencyHunter.Frontend;

internal sealed class SsaPlan
{
    private readonly IMethodSymbol _method;
    private readonly ISymbol? _initializerOwner;
    private readonly EffectiveFlowGraph _flowGraph;
    private readonly Dictionary<ISymbol, SsaVariable> _symbols = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<CaptureId, SsaVariable> _captures = [];

    /// <summary>The local or parameter each capture took, which a compound assignment through the capture defines.</summary>
    private readonly Dictionary<CaptureId, SsaVariable> _capturedVariables = [];
    private readonly List<SsaVariable> _variables = [];
    private readonly Dictionary<int, List<SsaDefinition>> _definitions = [];
    private readonly Dictionary<int, Dictionary<SsaVariable, SsaToken>> _entry = [];
    private readonly Dictionary<int, Dictionary<SsaVariable, SsaToken>> _exit = [];
    private readonly Dictionary<(int Block, SsaVariable Variable), SsaPhiToken> _phis = [];

    private SsaPlan(IMethodSymbol method, ControlFlowGraph graph, EffectiveFlowGraph flowGraph, ISymbol? initializerOwner)
    {
        _method = method;
        _initializerOwner = initializerOwner;
        _flowGraph = flowGraph;
        foreach (var parameter in method.Parameters)
        {
            WorkCancellation.Check();
            GetVariable(parameter, null);
        }
        foreach (var block in graph.Blocks)
        {
            WorkCancellation.Check();
            foreach (var operation in block.Operations)
            {
                WorkCancellation.Check();
                Scan(operation, block.Ordinal);
            }
            if (block.BranchValue is not null)
                Scan(block.BranchValue, block.Ordinal);
        }

        Compute(graph);
    }

    internal IReadOnlyList<SsaVariable> Variables => _variables;
    internal IReadOnlyList<SsaVariable> CapturedVariables => _variables.Where(variable => variable.IsOuterCapture).ToArray();

    /// <summary>The variables of an enclosing body this one captures and assigns.</summary>
    internal IReadOnlyList<SsaVariable> AssignedCaptures =>
        CapturedVariables.Where(variable => _definitions.Values.Any(definitions => definitions.Any(definition => definition.Variable == variable)))
                         .ToArray();
    internal SsaDefaultToken Default { get; } = new();
    internal SsaExceptionalToken Exceptional { get; } = new();

    /// <summary><paramref name="initializerOwner"/> is the field or property whose initializer <paramref name="graph"/> is, when
    /// the graph is part of <paramref name="method"/>'s constructor body: variables it declares are not captures.</summary>
    internal static SsaPlan Create(IMethodSymbol method, ControlFlowGraph graph, EffectiveFlowGraph flowGraph,
                                   ISymbol? initializerOwner = null) =>
        new(method, graph, flowGraph, initializerOwner);

    internal bool TryGetVariable(ISymbol symbol, out SsaVariable variable) =>
        _symbols.TryGetValue(symbol, out variable!);

    /// <summary>Whether a write of a local or a parameter defines a new version of it. A binding of a <c>ref</c> local or a
    /// <c>ref</c>, <c>in</c> or <c>out</c> parameter (<c>ref T r = ref …</c>, <c>r = ref …</c>, a <c>foreach (ref var r in …)</c>
    /// variable) does; a write through one — an assignment, a compound assignment, an increment, <c>??=</c>, a deconstruction into
    /// it, passing it on as a <c>ref</c> or <c>out</c> argument — writes the storage it refers to and does not; every write of a
    /// variable that is no reference does. This is the one owner of the rule. The plan asks it in <see cref="ScanTarget"/>,
    /// <see cref="DefineTarget"/>, the flow-capture branch of <see cref="Scan"/> and <see cref="ScanCall"/>; the lowering's
    /// consumers of planned definitions agree with it: <c>IrLowering.LowerAssignment</c> binds, <c>LowerStore</c> writes through,
    /// and <c>LowerArguments</c> defines no version for a reference passed on.</summary>
    /// <param name="variable">The local or parameter written.</param>
    /// <param name="binds">Whether the write binds the variable to a reference (<see cref="Binds"/>) rather than writing a value.</param>
    internal static bool DefinesVersion(ISymbol variable, bool binds) =>
        binds || variable is not (ILocalSymbol { RefKind: not RefKind.None } or IParameterSymbol { RefKind: not RefKind.None });

    /// <summary>Whether an assignment binds its target to a reference instead of writing a value: a <c>ref</c> assignment, as a
    /// <c>ref</c> local's declaration and a <c>foreach (ref var …)</c> variable are in the flow graph.</summary>
    /// <param name="assignment">The assignment.</param>
    internal static bool Binds(ISimpleAssignmentOperation assignment) => assignment.IsRef;

    internal SsaVariable GetVariable(CaptureId capture) => _captures[capture];

    internal SsaToken Entry(int blockOrdinal, SsaVariable variable) => _entry[blockOrdinal][variable];

    internal IReadOnlyList<(SsaVariable Variable, SsaPhiToken Token)> Phis(int blockOrdinal) =>
        _variables.Where(variable => _phis.ContainsKey((blockOrdinal, variable)))
                  .Select(variable => (variable, _phis[(blockOrdinal, variable)]))
                  .ToArray();

    internal IReadOnlyList<(EffectiveFlowGraph.FlowEdge Edge, SsaToken Token)> PhiInputs(
        int blockOrdinal, SsaVariable variable) =>
        _flowGraph.Predecessors(blockOrdinal)
                  .Select(edge => (edge, EdgeToken(edge, variable) ?? Initial(variable)))
                  .ToArray();

    internal IReadOnlyList<SsaDefinition> Definitions(int blockOrdinal) =>
        _definitions.GetValueOrDefault(blockOrdinal) ?? [];

    private void Compute(ControlFlowGraph graph)
    {
        foreach (var block in graph.Blocks)
        {
            WorkCancellation.Check();
            _entry.Add(block.Ordinal, []);
            _exit.Add(block.Ordinal, []);
        }

        var changed = true;
        while (changed)
        {
            WorkCancellation.Check();
            changed = false;
            foreach (var block in graph.Blocks)
            {
                WorkCancellation.Check();
                foreach (var variable in _variables)
                {
                    WorkCancellation.Check();
                    var incoming = Incoming(block.Ordinal, variable);
                    if (!_entry[block.Ordinal].TryGetValue(variable, out var oldEntry) || oldEntry != incoming)
                    {
                        _entry[block.Ordinal][variable] = incoming;
                        changed = true;
                    }

                    var outgoing = LastDefinition(block.Ordinal, variable)?.Token ?? incoming;
                    if (!_exit[block.Ordinal].TryGetValue(variable, out var oldExit) || oldExit != outgoing)
                    {
                        _exit[block.Ordinal][variable] = outgoing;
                        changed = true;
                    }
                }
            }
        }
    }

    private SsaToken Incoming(int blockOrdinal, SsaVariable variable)
    {
        var predecessors = _flowGraph.Predecessors(blockOrdinal);
        if (blockOrdinal == 0 || predecessors.Count == 0)
            return Initial(variable);

        var tokens = predecessors.Select(edge => EdgeToken(edge, variable))
                                 .Where(token => token is not null)
                                 .Cast<SsaToken>()
                                 .Distinct()
                                 .ToArray();
        if (tokens.Length == 0)
            return Initial(variable);
        if (tokens.Length == 1 && !_phis.ContainsKey((blockOrdinal, variable)))
            return tokens[0];
        return GetPhi(blockOrdinal, variable);
    }

    private SsaToken? EdgeToken(EffectiveFlowGraph.FlowEdge edge, SsaVariable variable)
    {
        if (edge.Kind == IrEdgeKind.Exceptional)
        {
            if (LastDefinition(edge.Source, variable) is not null)
                return Exceptional;
            return _entry[edge.Source].GetValueOrDefault(variable);
        }

        return _exit[edge.Source].GetValueOrDefault(variable);
    }

    private SsaToken Initial(SsaVariable variable) => variable.IsIncoming ? variable.Incoming : Default;

    private SsaPhiToken GetPhi(int blockOrdinal, SsaVariable variable)
    {
        if (!_phis.TryGetValue((blockOrdinal, variable), out var phi))
        {
            phi = new SsaPhiToken(blockOrdinal, variable);
            _phis.Add((blockOrdinal, variable), phi);
        }
        return phi;
    }

    private SsaDefinition? LastDefinition(int blockOrdinal, SsaVariable variable) =>
        _definitions.GetValueOrDefault(blockOrdinal)?.LastOrDefault(definition => definition.Variable == variable);

    private void Scan(IOperation operation, int blockOrdinal)
    {
        WorkCancellation.Check();
        switch (operation)
        {
            case IFlowAnonymousFunctionOperation:
            case ILocalFunctionOperation:
                return;
            case IVariableDeclaratorOperation declarator:
                if (declarator.Initializer is not null)
                {
                    Scan(declarator.Initializer.Value, blockOrdinal);
                    AddDefinition(blockOrdinal, GetVariable(declarator.Symbol, declarator));
                }
                else
                {
                    GetVariable(declarator.Symbol, declarator);
                }
                return;
            case ISimpleAssignmentOperation assignment:
                Scan(assignment.Value, blockOrdinal);
                ScanTarget(assignment.Target, blockOrdinal, Binds(assignment));
                return;
            case ICompoundAssignmentOperation compound:
                Scan(compound.Target, blockOrdinal);
                Scan(compound.Value, blockOrdinal);
                DefineTarget(compound.Target, blockOrdinal);
                return;
            case IIncrementOrDecrementOperation increment:
                Scan(increment.Target, blockOrdinal);
                DefineTarget(increment.Target, blockOrdinal);
                return;
            case IDeconstructionAssignmentOperation deconstruction:
                Scan(deconstruction.Value, blockOrdinal);
                DefineTarget(deconstruction.Target, blockOrdinal);
                return;
            case IIsPatternOperation pattern:
                Scan(pattern.Value, blockOrdinal);
                ScanPattern(pattern.Pattern, blockOrdinal);
                return;
            case ISwitchOperation @switch:
                Scan(@switch.Value, blockOrdinal);
                foreach (var clause in @switch.Cases.SelectMany(@case => @case.Clauses).OfType<IPatternCaseClauseOperation>())
                {
                    WorkCancellation.Check();
                    ScanPattern(clause.Pattern, blockOrdinal);
                    if (clause.Guard is not null)
                        Scan(clause.Guard, blockOrdinal);
                }
                foreach (var @case in @switch.Cases)
                {
                    WorkCancellation.Check();
                    foreach (var body in @case.Body)
                    {
                        WorkCancellation.Check();
                        Scan(body, blockOrdinal);
                    }
                }
                return;
            case ISwitchExpressionOperation @switch:
                Scan(@switch.Value, blockOrdinal);
                foreach (var arm in @switch.Arms)
                {
                    WorkCancellation.Check();
                    ScanPattern(arm.Pattern, blockOrdinal);
                    if (arm.Guard is not null)
                        Scan(arm.Guard, blockOrdinal);
                    Scan(arm.Value, blockOrdinal);
                }
                return;
            case IFlowCaptureOperation capture:
                Scan(capture.Value, blockOrdinal);
                // A write through the capture writes the variable it took; a reference is written through, never redefined.
                if (TryTargetVariable(Unwrap(capture.Value), out var captured) && DefinesVersion(captured.Symbol!, binds: false))
                    _capturedVariables[capture.Id] = captured;
                AddDefinition(blockOrdinal, GetVariable(capture.Id, capture.Value));
                return;
            case IFlowCaptureReferenceOperation capture:
                GetVariable(capture.Id, capture);
                return;
            case IInvocationOperation invocation:
                ScanCall(invocation, invocation.Arguments, blockOrdinal);
                return;
            case IObjectCreationOperation { Constructor: not null } creation:
                ScanCall(creation, creation.Arguments, blockOrdinal);
                return;
            case ILocalReferenceOperation local:
                GetVariable(local.Local, local);
                return;
            case IParameterReferenceOperation parameter when !IsPrimaryConstructorParameter(parameter.Parameter):
                GetVariable(parameter.Parameter, parameter);
                return;
        }

        foreach (var child in operation.ChildOperations)
        {
            WorkCancellation.Check();
            Scan(child, blockOrdinal);
        }
    }

    private void ScanPattern(IPatternOperation pattern, int blockOrdinal)
    {
        WorkCancellation.Check();
        switch (pattern)
        {
            case IDeclarationPatternOperation { DeclaredSymbol: { } symbol }:
                AddDefinition(blockOrdinal, GetVariable(symbol, pattern));
                return;
            case IRecursivePatternOperation recursive:
                if (recursive.DeclaredSymbol is { } declared)
                    AddDefinition(blockOrdinal, GetVariable(declared, recursive));
                foreach (var positional in recursive.DeconstructionSubpatterns)
                {
                    WorkCancellation.Check();
                    ScanPattern(positional, blockOrdinal);
                }
                foreach (var property in recursive.PropertySubpatterns)
                {
                    WorkCancellation.Check();
                    Scan(property.Member, blockOrdinal);
                    ScanPattern(property.Pattern, blockOrdinal);
                }
                return;
            case IListPatternOperation list:
                if (list.DeclaredSymbol is { } listSymbol)
                    AddDefinition(blockOrdinal, GetVariable(listSymbol, list));
                foreach (var element in list.Patterns)
                {
                    WorkCancellation.Check();
                    ScanPattern(element, blockOrdinal);
                }
                return;
            case ISlicePatternOperation slice:
                if (slice.Pattern is not null)
                    ScanPattern(slice.Pattern, blockOrdinal);
                return;
            case IBinaryPatternOperation binary:
                ScanPattern(binary.LeftPattern, blockOrdinal);
                ScanPattern(binary.RightPattern, blockOrdinal);
                return;
            case INegatedPatternOperation negated:
                ScanPattern(negated.Pattern, blockOrdinal);
                return;
        }

        foreach (var child in pattern.ChildOperations)
        {
            WorkCancellation.Check();
            Scan(child, blockOrdinal);
        }
    }

    /// <summary>A call defines a new version of every local or parameter it passes by <c>ref</c> or <c>out</c>, after its
    /// receiver and arguments are evaluated, save a reference passed on, which the call writes through (<see cref="DefinesVersion"/>).</summary>
    /// <param name="call">The invocation or object creation.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="blockOrdinal">The block the call stands in.</param>
    private void ScanCall(IOperation call, IEnumerable<IArgumentOperation> arguments, int blockOrdinal)
    {
        foreach (var child in call.ChildOperations)
        {
            WorkCancellation.Check();
            Scan(child, blockOrdinal);
        }
        foreach (var argument in arguments)
        {
            WorkCancellation.Check();
            if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out && RefArgumentVariable(argument) is { } variable &&
                DefinesVersion(variable.Symbol!, binds: false))
            {
                AddDefinition(blockOrdinal, variable);
            }
        }
    }

    private SsaVariable? RefArgumentVariable(IArgumentOperation argument) => Unwrap(argument.Value) switch
    {
        ILocalReferenceOperation local => GetVariable(local.Local, local),
        IParameterReferenceOperation parameter when !IsPrimaryConstructorParameter(parameter.Parameter) ||
                                                    SymbolEqualityComparer.Default.Equals(parameter.Parameter.ContainingSymbol, _method) =>
            GetVariable(parameter.Parameter, parameter),
        _ => null
    };

    private void ScanTarget(IOperation target, int blockOrdinal, bool binds)
    {
        WorkCancellation.Check();
        target = Unwrap(target);
        if (target is ITupleOperation tuple)
        {
            foreach (var element in tuple.Elements)
            {
                WorkCancellation.Check();
                ScanTarget(element, blockOrdinal, binds: false);
            }
            return;
        }

        if (TryTargetVariable(target, out var variable))
        {
            if (DefinesVersion(variable.Symbol!, binds))
                AddDefinition(blockOrdinal, variable);
            return;
        }

        // `x ??= value` assigns the local or parameter its target's capture took (question 68).
        if (target is IFlowCaptureReferenceOperation capture && _capturedVariables.TryGetValue(capture.Id, out var captured))
        {
            AddDefinition(blockOrdinal, captured);
            return;
        }

        if (target is IInvocationOperation invocation)
        {
            ScanCall(invocation, invocation.Arguments, blockOrdinal);
            return;
        }

        foreach (var child in target.ChildOperations)
        {
            WorkCancellation.Check();
            Scan(child, blockOrdinal);
        }
    }

    private void DefineTarget(IOperation target, int blockOrdinal)
    {
        WorkCancellation.Check();
        target = Unwrap(target);
        if (target is ITupleOperation tuple)
        {
            foreach (var element in tuple.Elements)
            {
                WorkCancellation.Check();
                DefineTarget(element, blockOrdinal);
            }
            return;
        }

        if (TryTargetVariable(target, out var variable))
        {
            if (DefinesVersion(variable.Symbol!, binds: false))
                AddDefinition(blockOrdinal, variable);
        }
        // A compound assignment whose right-hand side branches writes the local or parameter its target's capture took.
        else if (target is IFlowCaptureReferenceOperation capture && _capturedVariables.TryGetValue(capture.Id, out var captured))
            AddDefinition(blockOrdinal, captured);
    }

    private bool TryTargetVariable(IOperation target, out SsaVariable variable)
    {
        switch (target)
        {
            case ILocalReferenceOperation local:
                variable = GetVariable(local.Local, local);
                return true;
            case IParameterReferenceOperation parameter when !IsPrimaryConstructorParameter(parameter.Parameter):
                variable = GetVariable(parameter.Parameter, parameter);
                return true;
            default:
                variable = null!;
                return false;
        }
    }

    private void AddDefinition(int blockOrdinal, SsaVariable variable)
    {
        var definitions = _definitions.GetValueOrDefault(blockOrdinal);
        if (definitions is null)
        {
            definitions = [];
            _definitions.Add(blockOrdinal, definitions);
        }

        var ordinal = definitions.Count(definition => definition.Variable == variable) + 1;
        definitions.Add(new SsaDefinition(variable, new SsaDefinitionToken(blockOrdinal, variable, ordinal)));
    }

    private SsaVariable GetVariable(ISymbol symbol, IOperation? reference)
    {
        if (_symbols.TryGetValue(symbol, out var existing))
        {
            existing.FirstReference ??= reference;
            return existing;
        }

        var type = symbol switch
        {
            IParameterSymbol parameterSymbol => parameterSymbol.Type,
            ILocalSymbol local => local.Type,
            _ => null
        };
        var isOuterCapture = symbol is ILocalSymbol or IParameterSymbol &&
                             !SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, _method) &&
                             !SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, _initializerOwner);
        var variable = new SsaVariable(
            symbol,
            null,
            // A local the compiler synthesized, the flag of a `lock` statement among them, has no name of its own.
            symbol.Name ?? "",
            type,
            symbol is IParameterSymbol ? IrValueKind.Parameter : IrValueKind.Local,
            symbol is IParameterSymbol incomingParameter &&
                SymbolEqualityComparer.Default.Equals(incomingParameter.ContainingSymbol, _method) || isOuterCapture,
            isOuterCapture,
            reference);
        _symbols.Add(symbol, variable);
        _variables.Add(variable);
        return variable;
    }

    private SsaVariable GetVariable(CaptureId capture, IOperation reference)
    {
        if (_captures.TryGetValue(capture, out var existing))
            return existing;

        var variable = new SsaVariable(
            null,
            capture,
            $"capture:{_captures.Count + 1}",
            reference.Type,
            IrValueKind.Temporary,
            false,
            false,
            reference);
        _captures.Add(capture, variable);
        _variables.Add(variable);
        return variable;
    }

    private static IOperation Unwrap(IOperation operation)
    {
        WorkCancellation.Check();
        return operation switch
        {
            IDeclarationExpressionOperation declaration => Unwrap(declaration.Expression),
            IConversionOperation conversion => Unwrap(conversion.Operand),
            _ => operation
        };
    }

    private static bool IsPrimaryConstructorParameter(IParameterSymbol parameter) =>
        parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } &&
        parameter.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is ParameterSyntax { Parent.Parent: TypeDeclarationSyntax });
}

internal sealed class SsaVariable
{
    internal SsaVariable(ISymbol? symbol, CaptureId? capture, string name, ITypeSymbol? type, IrValueKind kind,
                         bool isIncoming, bool isOuterCapture, IOperation? firstReference)
    {
        Symbol = symbol;
        Capture = capture;
        Name = name;
        Type = type;
        Kind = kind;
        IsIncoming = isIncoming;
        IsOuterCapture = isOuterCapture;
        FirstReference = firstReference;
        Incoming = new SsaIncomingToken(this);
    }

    internal ISymbol? Symbol { get; }
    internal CaptureId? Capture { get; }
    internal string Name { get; }
    internal ITypeSymbol? Type { get; }
    internal IrValueKind Kind { get; }
    internal bool IsIncoming { get; }
    internal bool IsOuterCapture { get; }
    internal IOperation? FirstReference { get; set; }
    internal SsaIncomingToken Incoming { get; }
}

internal abstract record SsaToken;
internal sealed record SsaIncomingToken(SsaVariable Variable) : SsaToken;
internal sealed record SsaDefaultToken : SsaToken;
internal sealed record SsaExceptionalToken : SsaToken;
internal sealed record SsaDefinitionToken(int BlockOrdinal, SsaVariable Variable, int Ordinal) : SsaToken;
internal sealed record SsaPhiToken(int BlockOrdinal, SsaVariable Variable) : SsaToken;
internal sealed record SsaDefinition(SsaVariable Variable, SsaDefinitionToken Token);
