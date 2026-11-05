using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace ConcurrencyHunter.Frontend;

internal sealed class SsaPlan
{
    private const int ABSENT = 0;
    private const int DEFAULT = 1;
    private const int EXCEPTIONAL = 2;
    private const int FIRST_INCOMING = 3;

    private readonly IMethodSymbol _method;
    private readonly ISymbol? _initializerOwner;
    private readonly EffectiveFlowGraph _flowGraph;
    private readonly Dictionary<ISymbol, SsaVariable> _symbols = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<CaptureId, SsaVariable> _captures = [];

    /// <summary>The local or parameter each capture took, which a compound assignment through the capture defines.</summary>
    private readonly Dictionary<CaptureId, SsaVariable> _capturedVariables = [];
    private readonly List<SsaVariable> _variables = [];
    private readonly Dictionary<int, List<SsaDefinition>> _definitions = [];

    /// <summary>The tokens the fixpoint deals in, by code: none at <see cref="ABSENT"/>, then <see cref="Default"/>,
    /// <see cref="Exceptional"/>, each variable's incoming token in variable order, each block's last definition of each variable
    /// it defines, and the phis in the order the fixpoint creates them. A token has one code, so two codes are equal exactly when
    /// their tokens are.</summary>
    private readonly List<SsaToken?> _tokens = [];

    /// <summary>The code of each block's entry token of each variable, at <c>block * variables + variable</c>, and
    /// <see cref="ABSENT"/> until the fixpoint first visits the pair.</summary>
    private int[] _entry = [];

    /// <summary>Per block, the ordinals of the variables it defines, ascending.</summary>
    private int[][] _definedVariables = [];

    /// <summary>Per block, the code of its last definition of each variable in <see cref="_definedVariables"/>.</summary>
    private int[][] _lastDefinitions = [];

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

    internal SsaToken Entry(int blockOrdinal, SsaVariable variable) =>
        _tokens[_entry[checked(blockOrdinal * _variables.Count + OrdinalOf(variable))]] ??
        throw new KeyNotFoundException($"No entry was planned for '{variable.Name}' in block {blockOrdinal}.");

    internal IReadOnlyList<(SsaVariable Variable, SsaPhiToken Token)> Phis(int blockOrdinal)
    {
        var phis = new List<(SsaVariable Variable, SsaPhiToken Token)>();
        for (var variable = 0; variable < _variables.Count; variable++)
        {
            WorkCancellation.Check();
            if (PhiAt(blockOrdinal, variable) is { } phi)
                phis.Add((_variables[variable], phi));
        }
        return phis;
    }

    internal IReadOnlyList<(EffectiveFlowGraph.FlowEdge Edge, SsaToken Token)> PhiInputs(
        int blockOrdinal, SsaVariable variable)
    {
        var ordinal = OrdinalOf(variable);
        return _flowGraph.Predecessors(blockOrdinal)
                         .Select(edge => (edge, _tokens[EdgeToken(edge.Source, edge.Kind == IrEdgeKind.Exceptional, ordinal)] ??
                                                _tokens[Initial(ordinal)]!))
                         .ToArray();
    }

    internal IReadOnlyList<SsaDefinition> Definitions(int blockOrdinal) =>
        _definitions.GetValueOrDefault(blockOrdinal) ?? [];

    /// <summary>Computes every block's entry token of every variable: passes over the blocks in graph order and, in each block, over
    /// the variables in <see cref="Variables"/> order, until a pass changes no entry. A block's exit token of a variable is not kept:
    /// it is the block's last definition of the variable, otherwise its entry, and like the entry it is absent until the fixpoint
    /// first visits the pair. Every write of the plan happens here, during construction.</summary>
    /// <param name="graph">The graph whose blocks are planned.</param>
    private void Compute(ControlFlowGraph graph)
    {
        var blocks = graph.Blocks.Length;
        var width = _variables.Count;
        _tokens.AddRange([null, Default, Exceptional]);
        foreach (var variable in _variables)
        {
            WorkCancellation.Check();
            _tokens.Add(variable.Incoming);
        }

        _definedVariables = new int[blocks][];
        _lastDefinitions = new int[blocks][];
        for (var block = 0; block < blocks; block++)
        {
            WorkCancellation.Check();
            var definitions = Definitions(block);
            if (definitions.Count == 0)
            {
                _definedVariables[block] = [];
                _lastDefinitions[block] = [];
                continue;
            }

            var last = new SortedDictionary<int, SsaDefinitionToken>();
            foreach (var definition in definitions)
            {
                WorkCancellation.Check();
                last[definition.Variable.Ordinal] = definition.Token;
            }
            _definedVariables[block] = [.. last.Keys];
            _lastDefinitions[block] = new int[last.Count];
            var index = 0;
            foreach (var token in last.Values)
            {
                WorkCancellation.Check();
                _lastDefinitions[block][index++] = _tokens.Count;
                _tokens.Add(token);
            }
        }

        var starts = new int[blocks + 1];
        var sources = new List<int>();
        var exceptional = new List<bool>();
        for (var block = 0; block < blocks; block++)
        {
            WorkCancellation.Check();
            foreach (var edge in _flowGraph.Predecessors(block))
            {
                WorkCancellation.Check();
                sources.Add(edge.Source);
                exceptional.Add(edge.Kind == IrEdgeKind.Exceptional);
            }
            starts[block + 1] = sources.Count;
        }
        var edgeSources = sources.ToArray();
        var edgeExceptional = exceptional.ToArray();

        _entry = new int[checked(blocks * width)];
        var changed = true;
        while (changed)
        {
            WorkCancellation.Check();
            changed = false;
            for (var block = 0; block < blocks; block++)
            {
                WorkCancellation.Check();
                var row = block * width;
                var count = starts[block + 1] - starts[block];
                var blockSources = edgeSources.AsSpan(starts[block], count);
                var blockExceptional = edgeExceptional.AsSpan(starts[block], count);
                for (var variable = 0; variable < width; variable++)
                {
                    WorkCancellation.Check();
                    var incoming = Incoming(block, variable, blockSources, blockExceptional);
                    if (_entry[row + variable] != incoming)
                    {
                        _entry[row + variable] = incoming;
                        changed = true;
                    }
                }
            }
        }
    }

    /// <summary>The code of a block's entry token of a variable from its predecessors' tokens: the initial token at the entry block
    /// or without predecessors, or when no predecessor has a token yet; the one token when all that have one agree and no phi was
    /// created here before; otherwise this pair's phi, created at first need.</summary>
    /// <param name="block">The block's ordinal.</param>
    /// <param name="variable">The variable's ordinal.</param>
    /// <param name="sources">The block's predecessor edges' sources, in <see cref="EffectiveFlowGraph.Predecessors"/> order.</param>
    /// <param name="exceptional">Whether each of those edges is exceptional.</param>
    private int Incoming(int block, int variable, ReadOnlySpan<int> sources, ReadOnlySpan<bool> exceptional)
    {
        if (block == 0 || sources.Length == 0)
            return Initial(variable);

        var first = ABSENT;
        var several = false;
        for (var index = 0; index < sources.Length && !several; index++)
        {
            WorkCancellation.Check();
            var token = EdgeToken(sources[index], exceptional[index], variable);
            if (token == ABSENT || token == first)
                continue;
            if (first == ABSENT)
                first = token;
            else
                several = true;
        }

        if (first == ABSENT)
            return Initial(variable);
        if (!several && PhiAt(block, variable) is null)
            return first;
        return GetPhi(block, variable);
    }

    /// <summary>The code of the token an edge carries into its destination, or <see cref="ABSENT"/> while its source has none: an
    /// exceptional edge carries <see cref="Exceptional"/> when its source defines the variable, visited or not, and otherwise the
    /// source's entry; any other edge carries the source's exit, which is absent while its entry is.</summary>
    /// <param name="source">The edge's source block.</param>
    /// <param name="exceptional">Whether the edge is exceptional.</param>
    /// <param name="variable">The variable's ordinal.</param>
    private int EdgeToken(int source, bool exceptional, int variable)
    {
        var entry = _entry[source * _variables.Count + variable];
        var definition = LastDefinition(source, variable);
        if (exceptional)
            return definition != ABSENT ? EXCEPTIONAL : entry;
        return entry == ABSENT || definition == ABSENT ? entry : definition;
    }

    private int Initial(int variable) => _variables[variable].IsIncoming ? FIRST_INCOMING + variable : DEFAULT;

    private int GetPhi(int block, int variable)
    {
        if (PhiAt(block, variable) is not null)
            return _entry[block * _variables.Count + variable];
        _tokens.Add(new SsaPhiToken(block, _variables[variable]));
        return _tokens.Count - 1;
    }

    /// <summary>The phi of a block for a variable, if the fixpoint created one. A phi is written as its own pair's entry when it is
    /// created and stays the entry at every later visit: edge tokens, once present, stay present, so <see cref="Incoming"/> keeps
    /// finding one and returns the phi. The entry is therefore the record of the phi.</summary>
    /// <param name="block">The block's ordinal.</param>
    /// <param name="variable">The variable's ordinal.</param>
    private SsaPhiToken? PhiAt(int block, int variable) =>
        _tokens[_entry[block * _variables.Count + variable]] is SsaPhiToken phi && phi.BlockOrdinal == block &&
        phi.Variable == _variables[variable]
            ? phi
            : null;

    private int LastDefinition(int block, int variable)
    {
        var index = Array.BinarySearch(_definedVariables[block], variable);
        return index >= 0 ? _lastDefinitions[block][index] : ABSENT;
    }

    private int OrdinalOf(SsaVariable variable) =>
        variable.Ordinal < _variables.Count && _variables[variable.Ordinal] == variable
            ? variable.Ordinal
            : throw new KeyNotFoundException($"'{variable.Name}' is not a variable of this plan.");

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
            _variables.Count,
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
            _variables.Count,
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
    internal SsaVariable(int ordinal, ISymbol? symbol, CaptureId? capture, string name, ITypeSymbol? type, IrValueKind kind,
                         bool isIncoming, bool isOuterCapture, IOperation? firstReference)
    {
        Ordinal = ordinal;
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

    /// <summary>The variable's place in its plan's <see cref="SsaPlan.Variables"/>, given by the plan that creates it.</summary>
    internal int Ordinal { get; }
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
