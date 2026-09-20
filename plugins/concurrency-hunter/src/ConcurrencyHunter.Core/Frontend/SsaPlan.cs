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
            GetVariable(parameter, null);
        foreach (var block in graph.Blocks)
        {
            foreach (var operation in block.Operations)
                Scan(operation, block.Ordinal);
            if (block.BranchValue is not null)
                Scan(block.BranchValue, block.Ordinal);
        }

        Compute(graph);
    }

    internal IReadOnlyList<SsaVariable> Variables => _variables;
    internal IReadOnlyList<SsaVariable> CapturedVariables => _variables.Where(variable => variable.IsOuterCapture).ToArray();
    internal SsaDefaultToken Default { get; } = new();
    internal SsaExceptionalToken Exceptional { get; } = new();

    /// <summary><paramref name="initializerOwner"/> is the field or property whose initializer <paramref name="graph"/> is, when
    /// the graph is part of <paramref name="method"/>'s constructor body: variables it declares are not captures.</summary>
    internal static SsaPlan Create(IMethodSymbol method, ControlFlowGraph graph, EffectiveFlowGraph flowGraph,
                                   ISymbol? initializerOwner = null) =>
        new(method, graph, flowGraph, initializerOwner);

    internal bool TryGetVariable(ISymbol symbol, out SsaVariable variable) =>
        _symbols.TryGetValue(symbol, out variable!);

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
            _entry.Add(block.Ordinal, []);
            _exit.Add(block.Ordinal, []);
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in graph.Blocks)
            {
                foreach (var variable in _variables)
                {
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
                ScanTarget(assignment.Target, blockOrdinal);
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
            case IFlowCaptureOperation capture:
                Scan(capture.Value, blockOrdinal);
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
            Scan(child, blockOrdinal);
    }

    /// <summary>A call defines a new version of every local or parameter it passes by <c>ref</c> or <c>out</c>, after its
    /// receiver and arguments are evaluated.</summary>
    private void ScanCall(IOperation call, IEnumerable<IArgumentOperation> arguments, int blockOrdinal)
    {
        foreach (var child in call.ChildOperations)
            Scan(child, blockOrdinal);
        foreach (var argument in arguments)
        {
            if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out && RefArgumentVariable(argument) is { } variable)
                AddDefinition(blockOrdinal, variable);
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

    private void ScanTarget(IOperation target, int blockOrdinal)
    {
        target = Unwrap(target);
        if (target is ITupleOperation tuple)
        {
            foreach (var element in tuple.Elements)
                ScanTarget(element, blockOrdinal);
            return;
        }

        if (TryTargetVariable(target, out var variable))
        {
            AddDefinition(blockOrdinal, variable);
            return;
        }

        foreach (var child in target.ChildOperations)
            Scan(child, blockOrdinal);
    }

    private void DefineTarget(IOperation target, int blockOrdinal)
    {
        target = Unwrap(target);
        if (target is ITupleOperation tuple)
        {
            foreach (var element in tuple.Elements)
                DefineTarget(element, blockOrdinal);
            return;
        }

        if (TryTargetVariable(target, out var variable))
            AddDefinition(blockOrdinal, variable);
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

    private static IOperation Unwrap(IOperation operation) => operation switch
    {
        IDeclarationExpressionOperation declaration => Unwrap(declaration.Expression),
        IConversionOperation conversion => Unwrap(conversion.Operand),
        _ => operation
    };

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
