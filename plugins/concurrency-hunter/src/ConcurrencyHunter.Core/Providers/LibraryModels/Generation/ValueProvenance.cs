using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The place in a generated model entry where an observed object has to be named.</summary>
public enum ValuePlace
{
    /// <summary>The complete result of the member.</summary>
    WholeResult,

    /// <summary>A value inside a result form.</summary>
    ResultValue,

    /// <summary>A value kept by a keeper.</summary>
    KeptValue,

    /// <summary>The complete value assigned to an <c>out</c> or <c>ref</c> argument.</summary>
    WholeOutput,

    /// <summary>A value inside an output form.</summary>
    OutputValue,

    /// <summary>A value written into an array argument.</summary>
    Store,

    /// <summary>The complete value handed to one delegate parameter.</summary>
    WholeDelegateInput,

    /// <summary>The objects a call's member runs on, or the value one of its parameters is handed.</summary>
    CallValue
}

/// <summary>Names every object observed by a generated model entry and derives its result, keeps, outputs, stores and delegate
/// inputs. This is the one owner of value provenance: all names come from allocation origin and the place where the value was
/// observed.</summary>
public sealed class ValueProvenance
{
    internal const int KEEPING_PATH_DEPTH = 8;
    private const string ELEMENT = PathValue.ELEMENT;
    private const string KEYS = PathValue.KEYS;

    private readonly Driver _driver;
    private readonly IReadOnlyDictionary<string, ClassifiedFate> _fates;
    private readonly RunContext _fate;
    private readonly RunContext? _confirmation;
    private readonly Dictionary<string, HashSet<string>> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KeeperValues> _keptByRegion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<LibraryValue>> _symbolicKeeps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlySet<string>> _keepingChain = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<LibraryValue>> _symbolicOutputs = new(StringComparer.Ordinal);
    private readonly List<RawInput> _rawInputs = [];
    private readonly Dictionary<string, HashSet<string>> _holderTriggers = new(StringComparer.Ordinal);
    private readonly GenerationCauses _causes = new();
    private readonly GenerationChecks _checks;
    private readonly StageChecks _stage;

    /// <summary>Reads value provenance from the fate run and, for holder inputs only, its confirmation run.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The fate run.</param>
    /// <param name="fates">The confirmed fates by member parameter.</param>
    /// <param name="confirmation">The holder confirmation run, or <c>null</c>.</param>
    public ValueProvenance(Driver driver, ScopeRun run, IReadOnlyDictionary<string, ClassifiedFate> fates, ScopeRun? confirmation)
        : this(driver, run, fates, confirmation, new GenerationChecks(CancellationToken.None))
    {
    }

    /// <summary>Reads value provenance as a stage of a generation: it checks before each instance it looks through and each value it
    /// observes.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The fate run.</param>
    /// <param name="fates">The confirmed fates by member parameter.</param>
    /// <param name="confirmation">The holder confirmation run, or <c>null</c>.</param>
    /// <param name="checks">The generation's checks, which cancel the reading.</param>
    internal ValueProvenance(Driver driver, ScopeRun run, IReadOnlyDictionary<string, ClassifiedFate> fates, ScopeRun? confirmation,
                             GenerationChecks checks)
    {
        if (run.Stopped)
            throw new ArgumentException("A stopped run has no values to name.", nameof(run));
        if (confirmation is { Stopped: true })
            confirmation = null;

        _checks = checks;
        _stage = new StageChecks(checks, GenerationStage.ReadEffects);
        _driver = driver;
        _fates = fates;
        _fate = new RunContext(driver, run, checks);
        _confirmation = confirmation is null ? null : new RunContext(driver, confirmation, checks);

        foreach (var instance in _fate.CallInstances())
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (run.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) &&
                body.Blocks.SelectMany(block => block.Operations).OfType<IrUnknownOperation>().FirstOrDefault(UnobservedStore) is { } store)
            {
                _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.UNSUPPORTED_STORE, $"in {instance.BodyId} at line {store.Provenance.Span.StartLine}");
                break;
            }
        }
        if (CarriedProbeFired() is { } carrier)
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.CARRIED_PROBE_FIRED, carrier);

        var rawResult = ResultRegions();
        var rawOutputs = OutputRegions();
        var rawStores = StoreRegions();
        ReadInputs();
        ReadKeeps(rawResult);

        _checks.Check(GenerationStage.ReadEffects);
        Observe(Shown(rawResult, _fate, ValuePlace.WholeResult), "result");
        foreach (var (parameter, regions) in rawOutputs)
        {
            _checks.Check(GenerationStage.ReadEffects);
            Observe(Shown(regions, _fate, ValuePlace.WholeOutput), $"output:{parameter}");
        }
        foreach (var (parameter, regions) in rawStores)
        {
            _checks.Check(GenerationStage.ReadEffects);
            Observe(regions, $"store:{parameter}");
        }
        foreach (var input in _rawInputs)
        {
            _checks.Check(GenerationStage.ReadEffects);
            Observe(Shown(input.Regions, input.Context, ValuePlace.WholeDelegateInput), $"input:{input.Parameter}:{input.Index}");
        }
        foreach (var keeper in _keptByRegion.Values)
        {
            _checks.Check(GenerationStage.ReadEffects);
            Observe(keeper.Regions, $"keeps:{keeper.Name}");
        }

        _checks.Check(GenerationStage.ReadEffects);
        Keeps = BuildKeeps();
        Result = BuildMemberResult(rawResult);
        Keeps = WithoutCollectedKeeper(Keeps, rawResult);
        Outputs = BuildOutputs(rawOutputs);
        Stores = BuildStores(rawStores);
        Inputs = BuildInputs();
        Calls = BuildCalls();
        HolderTriggers = BuildHolderTriggers();
        KeepingChain = _keepingChain.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        _causes.Add(new[] { _fate.Unobserved, _confirmation?.Unobserved }.OfType<GenerationCause>());
        Causes = _causes.All;
        Reason = _causes.Reason;
    }

    /// <summary>What the member returns; <c>null</c> for a constructor, a void-like result or an unnameable result.</summary>
    public LibraryResult? Result { get; }

    /// <summary>The values kept by <c>this</c>, a library-class argument, or a fresh result.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> Keeps { get; }

    /// <summary>The result form assigned to every changed <c>out</c> or <c>ref</c> argument.</summary>
    public IReadOnlyDictionary<string, LibraryResult> Outputs { get; }

    /// <summary>The values written into each array argument's cells.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> Stores { get; }

    /// <summary>For each classified fate, the values handed to each parameter of its probe delegate.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<LibraryValue>>> Inputs { get; }

    /// <summary>The members of the user's objects the call runs: each equality, hash or ordering member a witness stood for in the
    /// call's tree, on the objects it ran on, with what each of its parameters got.</summary>
    public IReadOnlyList<LibraryMemberCall> Calls { get; }

    /// <summary>For each holder fate, the declaration ids of the holder members whose trigger fired its probe.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> HolderTriggers { get; }

    /// <summary>For each keeper region that keeps something, the keeper and every pre-existing region on a keeping path.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> KeepingChain { get; }

    /// <summary><c>vocabulary</c> when an observed object has no name; otherwise <c>null</c>.</summary>
    public string? Reason { get; }

    /// <summary>Every case found that gives that reason, one per code, sorted by code.</summary>
    public IReadOnlyList<GenerationCause> Causes { get; }

    /// <summary>The name of a region in the fate run at one entry place, or <c>null</c> when the vocabulary has none.</summary>
    /// <param name="region">The observed region.</param>
    /// <param name="place">Where it was observed.</param>
    // bounded: overload forwarding terminates at the implementation overload.
    public LibraryValue? Name(string region, ValuePlace place) => Name(region, place, _fate, ExpectedAction(place));

    /// <summary>The values a keeper region keeps, named by the model grammar.</summary>
    /// <param name="region">The keeper region.</param>
    public IReadOnlyList<LibraryValue> KeptBy(string region) =>
        _keptByRegion.TryGetValue(region, out var kept)
            ? Order(Names(kept.Regions, ValuePlace.KeptValue, _fate, null).Concat(_symbolicKeeps.GetValueOrDefault(region) ?? []))
            : [];

    /// <summary>Whether a region is one of the pre-existing objects on a path from a keeper to a value it keeps. An object the call
    /// made is never one: the walk goes through it to the value, but it was no state before the call (R6).</summary>
    /// <param name="region">The region written by the member.</param>
    public bool IsInKeepingChain(string region)
    {
        if (_fate.Allocations.CreatedByCall(region))
            return false;
        var chain = KeepingChain.Values.SelectMany(value => value).ToHashSet(StringComparer.Ordinal);
        if (chain.Contains(region) || !_fate.Heap.Regions.TryGetValue(region, out var stored))
            return chain.Contains(region);
        if (chain.Any(candidate => _fate.Heap.Regions.TryGetValue(candidate, out var kept) && kept.Group == stored.Group))
            return true;
        return KeepingChain.Any(pair => SharesPath(pair.Key, region, pair.Value, _fate.Reachability.Edges));
    }

    /// <summary>The driver action other than setup an execution runs for, as value provenance reads a run: the action of an own or a
    /// child execution — <c>V_Call</c>, <c>V_Enum</c> or a trigger — and <c>null</c> for setup, the artefact and elsewhere.</summary>
    /// <param name="executions">The run's driver executions.</param>
    /// <param name="execution">The execution.</param>
    internal static string? ActionOf(DriverExecutions executions, string execution)
    {
        var role = executions.Of(execution);
        return role is { Role: DriverExecutionRole.Own or DriverExecutionRole.Child, Action: not null } ? role.Action : null;
    }

    /// <summary>Whether one field path from a keeper, at most <see cref="KEEPING_PATH_DEPTH"/> fields long, reaches both a region and
    /// one of a set of regions. The two walks along a path go in step, as pairs of the objects each has reached, and a pair is
    /// expanded once, at the depth it is first reached, where it has the most of the depth left: the paths themselves are never
    /// spelled out, and a dense object graph, whose paths grow as its fields to the power of the depth, costs its pairs.</summary>
    /// <param name="keeper">The keeper the paths start from.</param>
    /// <param name="region">The region the path must reach.</param>
    /// <param name="candidates">The regions one of which the same path must reach.</param>
    /// <param name="edges">The edges out of a region, each with the field it goes through.</param>
    internal static bool SharesPath(string keeper, string region, IReadOnlySet<string> candidates,
                                    Func<string, IReadOnlyList<(string Field, string Target)>> edges)
    {
        var seen = new HashSet<(string Stored, string Candidate)> { (keeper, keeper) };
        var level = new List<(string Stored, string Candidate)> { (keeper, keeper) };
        for (var depth = 0; level.Count != 0; depth++)
        {
            WorkCancellation.Check();
            if (level.Any(pair => pair.Stored == region && candidates.Contains(pair.Candidate)))
                return true;
            if (depth == KEEPING_PATH_DEPTH)
                return false;
            var next = new List<(string Stored, string Candidate)>();
            foreach (var (stored, candidate) in level)
            {
                WorkCancellation.Check();
                var candidateTargets = edges(candidate).ToLookup(edge => edge.Field, edge => edge.Target, StringComparer.Ordinal);
                foreach (var (field, storedTarget) in edges(stored))
                {
                    WorkCancellation.Check();
                    foreach (var candidateTarget in candidateTargets[field])
                    {
                        WorkCancellation.Check();
                        if (seen.Add((storedTarget, candidateTarget)))
                            next.Add((storedTarget, candidateTarget));
                    }
                }
            }

            level = next;
        }

        return false;
    }

    private LibraryResult? BuildMemberResult(IReadOnlySet<string> regions)
    {
        if (_driver.Member.MethodKind == MethodKind.Constructor || ObservedResultType() is not { } type)
            return null;
        if (_fates.Values.Any(fate => fate.Fate == FateClassifier.HOLDER && fate.Holder == FateClassifier.RESULT))
            return null;
        // A task's result is task(X) at each level, X what its innermost completion value is named as the result of a member
        // returning that type; the regions are the completion objects the driver's await holds.
        if (TaskTypes.Depth(_driver.Member.ReturnType) is var depth and > 0)
        {
            // What the member returns names its result when every value it returns has a name: a task argument returned itself is
            // [arg:p]; an async body returns what its own task completes with, completion(arg:p) for an awaited argument, inside task(…).
            if (SymbolicReturnNames(complete: true) is { Count: > 0 } returned)
            {
                var named = new LibraryResult(LibraryResultKind.OneOf, returned);
                return _driver.Member.IsAsync ? new LibraryResult(LibraryResultKind.Task, [], named) : named;
            }
            // A task the member returns but does not make is named by itself at the level it stands: the argument's task returned
            // through FromResult or WhenAny is [arg:p] inside the levels the member makes, never a task made anew around its completion.
            if (NamedTaskLevel(depth) is { } existing)
                return existing;
            if (regions.Count == 0 && type.ContainingAssembly?.Name == DriverSynthesizer.ASSEMBLY)
            {
                _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.RESULT_UNRESOLVED, $"the completion value, a {type.Name}, is no object");
                return null;
            }
            var completion = ResultOf(regions, type, ValuePlace.WholeResult, ValuePlace.ResultValue, _fate, DriverSynthesizer.CALL);
            for (var level = 0; completion is not null && level < depth; level++)
            {
                _checks.Check(GenerationStage.ReadEffects);
                completion = new LibraryResult(LibraryResultKind.Task, [], completion);
            }
            return completion;
        }
        // What a member of a type that holds no object returns — node.GetHashCode(), list.Count, ToString() — names nothing.
        if (!CanHoldObject(type))
            return null;
        if (regions.Count == 0 && SymbolicReturnNames() is { Count: > 0 } symbolic)
            return new LibraryResult(LibraryResultKind.OneOf, symbolic);
        if (regions.Count == 0 && !type.IsValueType && ReturnedNestedAssigned() is { } variable)
        {
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.RESULT_UNRESOLVED,
                        $"what {variable.BodyId} returns is {variable.Name}, which a nested body assigns, and none of it names an object");
            return null;
        }
        if (regions.Count == 0 && type.ContainingAssembly?.Name == DriverSynthesizer.ASSEMBLY)
        {
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.RESULT_UNRESOLVED, $"the result, a {type.Name}, is no object");
            return null;
        }
        if (regions.Count == 0 &&
            _fate.MemberInstances(DriverSynthesizer.CALL).FirstOrDefault(instance => instance.Summary.Returns.Any(returned => returned.Values.Count != 0)) is
            { } returning)
        {
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.RESULT_UNRESOLVED, $"what {returning.BodyId} returns is no object");
            return null;
        }
        return ResultOf(regions, type, ValuePlace.WholeResult, ValuePlace.ResultValue, _fate, DriverSynthesizer.CALL);
    }

    /// <summary>The result of a task member named at the first level whose tasks all have a name of their own, inside one <c>task(…)</c>
    /// for every level above it: the member's call gives level 0, and each level's tasks complete with the next one's. <c>null</c> when
    /// no level above the innermost completion value is named so, and the innermost value names the result instead.</summary>
    /// <param name="depth">How many tasks stand around the member's innermost completion value.</param>
    private LibraryResult? NamedTaskLevel(int depth)
    {
        IReadOnlySet<string> tasks = _fate.Heap.Instances.Values
                                          .Where(instance => instance.BodyId == ValueObservation.DriverBody(DriverSynthesizer.CALL))
                                          .SelectMany(instance => _fate.Heap.Edges.Where(edge => edge.CallerInstance == instance.Id &&
                                                                                                 _fate.Heap.Instances.TryGetValue(edge.CalleeInstance, out var callee) &&
                                                                                                 _fate.MemberInstances(DriverSynthesizer.CALL).Contains(callee))
                                                                                   .SelectMany(edge => _fate.Heap.Resolve(instance.Id, new CallResultValue(edge.OperationId))))
                                          .ToHashSet(StringComparer.Ordinal);
        for (var level = 0; level < depth && tasks.Count != 0; level++)
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (tasks.All(task => _fate.RootNames(task).Count != 0))
            {
                LibraryResult named = new(LibraryResultKind.OneOf, Names(tasks, ValuePlace.ResultValue, _fate, null, markMissing: false));
                for (var above = 0; above < level; above++)
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    named = new LibraryResult(LibraryResultKind.Task, [], named);
                }
                return named;
            }
            tasks = tasks.SelectMany(_fate.Heap.Completion).ToHashSet(StringComparer.Ordinal);
        }
        return null;
    }

    /// <summary>A variable the member returns that a nested body of it assigns: the member may return a version it read before that body
    /// ran, which names nothing the body put there (question 153). Null when it returns no such variable.</summary>
    private (string BodyId, string Name)? ReturnedNestedAssigned()
    {
        var bodies = _fate.Run.Reachable.Bodies;
        foreach (var body in _fate.MemberInstances(DriverSynthesizer.CALL).Select(instance => bodies.GetValueOrDefault(instance.BodyId)).OfType<IrBody>().Distinct())
        {
            _checks.Check(GenerationStage.ReadEffects);
            var nested = bodies.Values.Where(other => other.BodyId.StartsWith(body.BodyId + "#", StringComparison.Ordinal)).ToArray();
            if (nested.Length == 0)
                continue;
            var values = body.Values.ToDictionary(value => value.Id);
            var definitions = body.Blocks.SelectMany(block => block.Operations)
                                  .SelectMany(operation => operation.DefinedValues.Select(value => (Value: value, Operation: operation)))
                                  .ToDictionary(pair => pair.Value, pair => pair.Operation);
            var pending = new Stack<int>(body.Blocks.SelectMany(block => block.Operations).OfType<IrReturnOperation>()
                                             .Select(returned => returned.Value).OfType<int>());
            var seen = new HashSet<int>();
            while (pending.TryPop(out var current))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (!seen.Add(current))
                    continue;
                var definition = definitions.GetValueOrDefault(current);
                var key = (definition as IrLoadCapturedOperation)?.SymbolKey ?? values[current].SymbolKey;
                if (key is not null && nested.Any(other => AssignsVariable(other, key)))
                    return (body.BodyId, values[current].Name);
                switch (definition)
                {
                    case IrAssignOperation assign:
                        pending.Push(assign.SourceValue);
                        break;
                    case IrConvertOperation convert:
                        pending.Push(convert.OperandValue);
                        break;
                    case IrPhiOperation phi:
                        foreach (var input in phi.Inputs)
                        {
                            _checks.Check(GenerationStage.ReadEffects);
                            pending.Push(input.Value);
                        }
                        break;
                }
            }
        }
        return null;
    }

    /// <summary>Whether a body assigns a version of a variable.</summary>
    /// <param name="body">The body.</param>
    /// <param name="key">The variable's key.</param>
    private static bool AssignsVariable(IrBody body, string key)
    {
        var versions = body.Values.Where(value => value.SymbolKey == key).Select(value => value.Id).ToHashSet();
        return versions.Count != 0 && body.Blocks.SelectMany(block => block.Operations)
                                          .Any(operation => operation is IrAssignOperation or IrCombineDelegatesOperation &&
                                                            operation.DefinedValues.Any(versions.Contains));
    }

    private ITypeSymbol? ObservedResultType() =>
        _driver.Compilation.GetTypeByMetadataName(DriverSynthesizer.KEEP_TYPE)?.GetMembers("R").OfType<IFieldSymbol>()
               .SingleOrDefault()?.Type ?? FateClassifier.ResultType(_driver.Member);

    /// <summary>Whether an unsupported operation assigns storage visible beyond a by-value local.</summary>
    /// <param name="operation">The operation the lowering could not express.</param>
    private bool UnobservedStore(IrUnknownOperation operation)
    {
        var span = operation.Provenance.Span;
        foreach (var reference in _driver.Compilation.References.OfType<CompilationReference>())
        {
            _checks.Check(GenerationStage.ReadEffects);
            foreach (var tree in reference.Compilation.SyntaxTrees.Where(tree => span.Path.Replace('\\', '/')
                .EndsWith(tree.FilePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            {
                _checks.Check(GenerationStage.ReadEffects);
                var text = tree.GetText();
                var start = text.Lines[span.StartLine - 1].Start + span.StartColumn - 1;
                var end = text.Lines[span.EndLine - 1].Start + span.EndColumn - 1;
                var node = tree.GetRoot().FindNode(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(start, end));
                if (reference.Compilation.GetSemanticModel(tree).GetOperation(node) is not IAssignmentOperation assignment)
                    continue;
                return assignment.Target switch
                {
                    IDiscardOperation => false,
                    ILocalReferenceOperation local => local.Local.RefKind != RefKind.None,
                    IParameterReferenceOperation parameter => parameter.Parameter.RefKind != RefKind.None,
                    _ => true
                };
            }
        }
        return false;
    }

    /// <summary>The names of what the member's call returns, from its own frame.</summary>
    /// <param name="complete">Whether to answer nothing unless every returned value has a name.</param>
    private IReadOnlyList<LibraryValue> SymbolicReturnNames(bool complete = false)
    {
        var values = new List<LibraryValue>();
        foreach (var instance in _fate.MemberInstances(DriverSynthesizer.CALL))
        {
            _checks.Check(GenerationStage.ReadEffects);
            foreach (var value in instance.Summary.Returns.SelectMany(returned => returned.Values))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (SymbolicName(value, instance) is { } named)
                    values.Add(named);
                else if (complete)
                    return [];
            }
        }
        return Order(values);
    }

    /// <summary>The name an entry gives a value of the member's own frame, or <c>null</c> when it has none. A delegate parameter
    /// never has one: a fate speaks for it, and TD-034a refuses <c>arg:P</c> for a delegate-typed <c>P</c> (task 11). What an
    /// <c>await</c> of a task argument yields is named through the argument (<see cref="CompletionOf"/>).</summary>
    /// <param name="value">The value.</param>
    /// <param name="instance">The member's instance expressing the value, which says what an <c>await</c> awaited; <c>null</c> names
    /// no awaited value.</param>
    private LibraryValue? SymbolicName(AbstractValue value, MethodInstance? instance = null)
    {
        _checks.Check(GenerationStage.ReadEffects);
        return value switch
        {
            ParameterValue parameter when parameter.Ordinal >= 0 && parameter.Ordinal < _driver.Member.Parameters.Length &&
                                          TypeShape.Of(_driver.Member.Parameters[parameter.Ordinal].Type) != TypeShapeKind.Delegate =>
                new ArgumentValue(_driver.Member.Parameters[parameter.Ordinal].Name),
            ConcurrencyHunter.Heap.ThisValue => new ThisValue(),
            PathValue path when SymbolicName(path.Base, instance) is { } root && path.Segments.All(segment => segment is ELEMENT or KEYS) =>
                path.Segments.Aggregate(root, (current, _) => (LibraryValue)new ElementsValue(current)),
            AwaitResultValue when instance is not null && SymbolicTask(value, instance) is { } awaited => awaited.Name,
            _ => null
        };
    }

    /// <summary>A value of the member's own frame that is a task argument or what awaiting one yields, at any depth, with its type.</summary>
    /// <param name="value">The value.</param>
    /// <param name="instance">The member's instance expressing the value.</param>
    private (LibraryValue Name, ITypeSymbol Type)? SymbolicTask(AbstractValue value, MethodInstance instance)
    {
        _checks.Check(GenerationStage.ReadEffects);
        return value switch
        {
            ParameterValue parameter when parameter.Ordinal >= 0 && parameter.Ordinal < _driver.Member.Parameters.Length =>
                (new ArgumentValue(_driver.Member.Parameters[parameter.Ordinal].Name), _driver.Member.Parameters[parameter.Ordinal].Type),
            AwaitResultValue awaited when instance.Summary.Joins.Where(join => join.OperationId == awaited.OperationId).SelectMany(join => join.Handles)
                                                  .SelectMany(handle => handle.Values).Distinct().ToArray() is [var task] &&
                                          SymbolicTask(task, instance) is { } named => CompletionOf(named.Name, named.Type),
            _ => null
        };
    }

    /// <summary>The one name of a value reached through a task argument: what a task's completion slot holds is <c>completion(…)</c>
    /// around the task's own name, the argument's task itself being <c>arg:p</c>, and a <c>Task&lt;Task&lt;T&gt;&gt;</c> argument
    /// awaited twice <c>completion(completion(arg:p))</c> (R6). <c>null</c> when the type is no <c>Task&lt;T&gt;</c> or
    /// <c>ValueTask&lt;T&gt;</c>, and when what it completes with is a delegate, which a fate speaks for as it does for a delegate
    /// argument.</summary>
    /// <param name="task">The task's name.</param>
    /// <param name="type">The task's type, or <c>null</c> when it is not known.</param>
    private static (LibraryValue Name, ITypeSymbol Type)? CompletionOf(LibraryValue task, ITypeSymbol? type) =>
        TaskTypes.CompletionType(type) is { } completion && TypeShape.Of(completion) != TypeShapeKind.Delegate
            ? (new CompletionValue(task), completion)
            : null;

    private IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> BuildKeeps()
    {
        var keeps = new SortedDictionary<string, IReadOnlyList<LibraryValue>>(StringComparer.Ordinal);
        foreach (var keeper in _keptByRegion.Values.OrderBy(keeper => keeper.Name, StringComparer.Ordinal))
        {
            _checks.Check(GenerationStage.ReadEffects);
            var symbolic = _symbolicKeeps.GetValueOrDefault(keeper.Region) ?? [];
            var values = Order(Names(keeper.Regions, ValuePlace.KeptValue, _fate, null).Concat(symbolic));
            if (values.Count != 0)
                keeps[keeper.Name] = values;
        }
        return keeps;
    }

    /// <summary>The keeps without the fresh result's when the result is named <c>collection(…)</c> or <c>dictionary(…)</c> and the
    /// result keeps nothing but the elements and keys that form names: the form names them, and <c>keeps.result</c> stands only
    /// beside a <c>new</c> result (R6). A result that keeps anything else — a comparer it was made with — stays a keeper.</summary>
    /// <param name="keeps">The keeps built.</param>
    /// <param name="rawResult">The result's regions.</param>
    private IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> WithoutCollectedKeeper(IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> keeps,
                                                                                            IReadOnlySet<string> rawResult)
    {
        if (Result?.Leaf.Kind is not (LibraryResultKind.Collection or LibraryResultKind.Dictionary))
            return keeps;
        foreach (var region in rawResult)
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (!_keptByRegion.TryGetValue(region, out var kept) || kept.Name != FateClassifier.RESULT || _symbolicKeeps.ContainsKey(region))
                continue;
            var collected = _fate.Heap.PointsTo(region, ELEMENT).Concat(_fate.Heap.PointsTo(region, KEYS)).ToHashSet(StringComparer.Ordinal);
            if (!kept.Regions.All(collected.Contains))
                continue;
            _keptByRegion.Remove(region);
            _keepingChain.Remove(region);
        }
        if (_keptByRegion.Values.Any(keeper => keeper.Name == FateClassifier.RESULT))
            return keeps;
        return new SortedDictionary<string, IReadOnlyList<LibraryValue>>(keeps.Where(pair => pair.Key != FateClassifier.RESULT)
                                                                              .ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal);
    }

    private IReadOnlyDictionary<string, LibraryResult> BuildOutputs(IReadOnlyDictionary<string, IReadOnlySet<string>> raw)
    {
        var outputs = new SortedDictionary<string, LibraryResult>(StringComparer.Ordinal);
        foreach (var (parameter, regions) in raw)
        {
            _checks.Check(GenerationStage.ReadEffects);
            var type = _driver.Member.Parameters.Single(candidate => candidate.Name == parameter).Type;
            var result = ResultOf(regions, type, ValuePlace.WholeOutput, ValuePlace.OutputValue, _fate, DriverSynthesizer.CALL);
            var symbolic = _symbolicOutputs.GetValueOrDefault(parameter) ?? [];
            if (symbolic.Count != 0)
            {
                if (result is null)
                    result = new LibraryResult(LibraryResultKind.OneOf, symbolic);
                else if (result.Kind == LibraryResultKind.OneOf)
                    result = new LibraryResult(LibraryResultKind.OneOf, Order(result.Values.Concat(symbolic)));
                else
                    _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.OUTPUT_FORMS_MIXED, $"{parameter}: {result.Kind} and named arguments");
            }
            if (result is not null)
                outputs[parameter] = result;
        }
        return outputs;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> BuildStores(IReadOnlyDictionary<string, IReadOnlySet<string>> raw)
    {
        var stores = new SortedDictionary<string, IReadOnlyList<LibraryValue>>(StringComparer.Ordinal);
        foreach (var (parameter, regions) in raw)
        {
            _checks.Check(GenerationStage.ReadEffects);
            var values = Names(regions, ValuePlace.Store, _fate, null);
            if (values.Count != 0)
                stores[parameter] = values;
        }
        return stores;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<LibraryValue>>> BuildInputs()
    {
        var inputs = new SortedDictionary<string, IReadOnlyList<IReadOnlyList<LibraryValue>>>(StringComparer.Ordinal);
        foreach (var group in _rawInputs.GroupBy(input => input.Parameter, StringComparer.Ordinal))
        {
            _checks.Check(GenerationStage.ReadEffects);
            var count = group.Max(input => input.Index) + 1;
            var parameters = new IReadOnlyList<LibraryValue>[count];
            for (var index = 0; index < count; index++)
            {
                _checks.Check(GenerationStage.ReadEffects);
                var at = group.Where(input => input.Index == index).ToArray();
                var values = new List<LibraryValue>();
                foreach (var input in at)
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    values.AddRange(Names(input.Regions, ValuePlace.WholeDelegateInput, input.Context, input.ExpectedActions));
                }
                parameters[index] = Order(values);
            }
            inputs[group.Key] = parameters;
        }
        return inputs;
    }

    /// <summary>The calls of the user's equality, hash and ordering members: a witness of one that ran in the call's tree, named by the
    /// objects it ran on and what each parameter got. One that ran only where the result is enumerated has no form, since a call's
    /// members run at the call.</summary>
    private IReadOnlyList<LibraryMemberCall> BuildCalls()
    {
        var calls = new List<LibraryMemberCall>();
        var expected = new HashSet<string>(StringComparer.Ordinal) { DriverSynthesizer.CALL };
        foreach (var instance in _fate.Heap.Instances.Values.Where(GenerationHandoffs.IsConventionWitness))
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (CalledMember(instance.BodyId) is not var (member, count))
                continue;
            var executions = _fate.Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>(StringComparer.Ordinal);
            if (!executions.Any(execution => _fate.Executions.InTree(execution, DriverSynthesizer.CALL)))
            {
                if (executions.Any(execution => _fate.Executions.InTree(execution, DriverSynthesizer.ENUMERATE)))
                    _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.CALL_AT_ENUMERATION, $"{member} in {instance.BodyId}");
                continue;
            }

            // A probe struct's witness got the probe objects the struct holds, which name it as a class probe's object names it (R9).
            var (receivers, parameters) = GenerationHandoffs.Received(_fate.Heap, instance);
            Observe(receivers, $"call:{member}");
            var inputs = new IReadOnlyList<LibraryValue>[count];
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                _checks.Check(GenerationStage.ReadEffects);
                var handed = parameters.GetValueOrDefault(ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
                Observe(handed, $"call:{member}:{ordinal}");
                inputs[ordinal] = Names(handed, ValuePlace.CallValue, _fate, expected);
            }
            foreach (var on in Names(receivers, ValuePlace.CallValue, _fate, expected))
            {
                _checks.Check(GenerationStage.ReadEffects);
                calls.Add(new LibraryMemberCall(member, on, inputs));
            }
        }
        return calls.DistinctBy(call => call.Canonical).OrderBy(call => call.Canonical, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The equality, hash or ordering member a witness body stands for, by name and parameter count, or <c>null</c> for any other
    /// member the analysis takes to read: a getter or <c>ToString</c>.</summary>
    /// <param name="bodyId">The witness's body.</param>
    private (string Member, int Count)? CalledMember(string bodyId)
    {
        if (_fate.Run.Program.Method(bodyId) is not { } method)
            return null;
        // An explicit implementation is named by the interface member it implements: System#IComparable{Probe_T}#CompareTo.
        var name = method.ImplementedInterfaceMethodIds.FirstOrDefault() is { } implemented ? MemberName(implemented) : method.Name;
        return LibraryMemberCall.Members.TryGetValue(name, out var counts) && counts.Contains(method.Parameters.Count)
            ? (name, method.Parameters.Count)
            : null;
    }

    /// <summary>The simple name of the member a method id names: <c>CompareTo</c> of
    /// <c>body:System.Private.CoreLib:M:System.IComparable`1.CompareTo(`0)</c>.</summary>
    /// <param name="methodId">The method id.</param>
    private static string MemberName(string methodId)
    {
        var id = methodId[methodId.IndexOf(":M:", StringComparison.Ordinal)..];
        var end = id.IndexOf('(') is var open and >= 0 ? open : id.Length;
        return id[(id.LastIndexOf('.', end - 1) + 1)..end];
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> BuildHolderTriggers()
    {
        var triggers = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (parameter, members) in _holderTriggers)
        {
            _checks.Check(GenerationStage.ReadEffects);
            triggers[parameter] = members.Order(StringComparer.Ordinal).ToArray();
        }
        return triggers;
    }

    private LibraryResult? ResultOf(IReadOnlySet<string> regions, ITypeSymbol type, ValuePlace wholePlace, ValuePlace valuePlace,
                                    RunContext context, string expectedAction)
    {
        if (regions.Count == 0)
            return null;

        var direct = Names(regions, valuePlace, context, null, markMissing: false);
        if (regions.All(region => context.RootNames(region).Count != 0))
            return new LibraryResult(LibraryResultKind.OneOf, direct);

        if (regions.Count != 1)
        {
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.RESULT_SEVERAL_OBJECTS,
                        $"{wholePlace}: {regions.Count} objects, {regions.First(region => context.RootNames(region).Count == 0)} unnamed");
            return null;
        }

        var region = regions.Single();
        // Elements none of which has a name are objects new shows with the collection (R5): only a collection some of whose elements
        // or keys have names refuses the rest as unnamed.
        var elements = context.Heap.PointsTo(region, ELEMENT);
        var keyed = context.Heap.PointsTo(region, KEYS);
        var named = elements.Concat(keyed).Any(element => context.RootNames(element).Count != 0);
        if (type is IArrayTypeSymbol || type is INamedTypeSymbol collection && SeedableFields.Collection(collection) is { Dictionary: false })
        {
            var values = Names(elements, valuePlace, context, null, markMissing: named);
            if (values.Count != 0)
                return new LibraryResult(LibraryResultKind.Collection, values);
        }

        if (type is INamedTypeSymbol dictionary && SeedableFields.Collection(dictionary) is { Dictionary: true })
        {
            var keys = Names(keyed, valuePlace, context, null, markMissing: named);
            var values = Names(elements, valuePlace, context, null, markMissing: named);
            if (keys.Count != 0 && values.Count != 0)
                return new LibraryResult(LibraryResultKind.Dictionary, [.. keys, .. values]);
        }

        var yields = context.Yields(region);
        if (yields.Count != 0 && IsSequence(type))
        {
            var values = Names(yields, valuePlace, context, null);
            return values.Count == 0 ? null : new LibraryResult(LibraryResultKind.Sequence, values);
        }

        if (Name(region, wholePlace, context, new HashSet<string>(StringComparer.Ordinal) { expectedAction }) is NewValue)
            return new LibraryResult(LibraryResultKind.New, []);

        _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.RESULT_UNNAMED, $"{wholePlace}: {region}");
        return null;
    }

    private void ReadKeeps(IReadOnlySet<string> rawResult)
    {
        var keepers = new List<(string Name, string Region)>();
        if (_driver.Member.MethodKind == MethodKind.Constructor)
        {
            keepers.AddRange(rawResult.Where(_fate.IsLibraryRegion).Select(region => (FateClassifier.THIS, region)));
        }
        else if (!_driver.Member.IsStatic)
        {
            keepers.AddRange(_fate.RootsOf(new ThisValue()).Select(region => (FateClassifier.THIS, region)));
        }

        foreach (var parameter in _driver.Member.Parameters)
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (parameter.RefKind == RefKind.Out || parameter.Type is IArrayTypeSymbol || parameter.Type is not INamedTypeSymbol named ||
                named.TypeKind != TypeKind.Class || SeedableFields.Collection(named) is not null)
            {
                continue;
            }
            keepers.AddRange(_fate.RootsOf(new ArgumentValue(parameter.Name)).Where(_fate.IsLibraryRegion)
                                  .Select(region => (parameter.Name, region)));
        }

        if (_driver.Member.MethodKind != MethodKind.Constructor)
        {
            keepers.AddRange(rawResult.Where(region => _fate.Allocations.CreatedByCall(region) && _fate.IsLibraryRegion(region))
                                      .Select(region => (FateClassifier.RESULT, region)));
        }
        foreach (var (name, region) in keepers.Distinct())
        {
            _checks.Check(GenerationStage.ReadEffects);
            ReadKeeper(name, region);
        }

        // What a task argument completes with is a keeper as the argument handed over directly would be: the walk reads the stores into
        // the object its innermost completion holds, at any depth. keeps.p keeps into the task itself, not into what it completes with,
        // so a keep found there has no name, and the member no model (R6, R7).
        foreach (var parameter in _driver.Member.Parameters.Where(parameter => parameter.RefKind != RefKind.Out && TaskTypes.Depth(parameter.Type) > 0))
        {
            _checks.Check(GenerationStage.ReadEffects);
            IReadOnlySet<string> carriers = _fate.RootsOf(new ArgumentValue(parameter.Name)).ToHashSet(StringComparer.Ordinal);
            carriers = HeapSolution.Completions(carriers, TaskTypes.Depth(parameter.Type), _fate.Heap.Completion);
            foreach (var carrier in carriers.Where(region => _fate.IsLibraryRegion(region) && !_keptByRegion.ContainsKey(region)))
            {
                _checks.Check(GenerationStage.ReadEffects);
                ReadKeeper(parameter.Name, carrier);
                if (!_keptByRegion.Remove(carrier))
                    continue;
                _keepingChain.Remove(carrier);
                _symbolicKeeps.Remove(carrier);
                _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.COMPLETION_KEEPER, $"{parameter.Name}: {carrier}");
            }
        }
    }

    private void ReadKeeper(string name, string keeper)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var ends = new HashSet<string>(StringComparer.Ordinal);
        var chain = new HashSet<string>(StringComparer.Ordinal);
        // Every step the walk takes, by its target: a state is walked once, but each path to a kept value is on the chain (R6).
        var predecessors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var seen = new HashSet<(string Region, bool Written)>();
        var pending = new Stack<(string Region, bool Written)>();
        foreach (var edge in _fate.Reachability.Edges(keeper))
        {
            _checks.Check(GenerationStage.ReadEffects);
            Push(pending, predecessors, keeper, edge.Target, _fate.WasWritten(new WrittenEdge(keeper, edge.Field, edge.Target)));
        }
        foreach (var target in _fate.DirectStores(keeper))
        {
            _checks.Check(GenerationStage.ReadEffects);
            Push(pending, predecessors, keeper, target, true);
        }
        foreach (var @object in _fate.Reachability.From([keeper]))
        {
            _checks.Check(GenerationStage.ReadEffects);
            foreach (var target in _fate.DirectElementStores(@object))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (@object != keeper)
                    AddPredecessor(predecessors, keeper, @object);
                Push(pending, predecessors, @object, target, true);
            }
        }

        while (pending.TryPop(out var state))
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (!seen.Add((state.Region, state.Written)))
                continue;
            if (_fate.IsProbeDelegate(state.Region))
                continue;

            var roots = _fate.RootNames(state.Region);
            if (roots.Count != 0 && state.Region != keeper)
            {
                if (state.Written)
                {
                    if (name == FateClassifier.RESULT && roots.Any(root => root is ThisValue))
                        _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.RESULT_KEEPS_RECEIVER, $"{keeper} keeps {state.Region}");
                    else
                        kept.Add(state.Region);
                    ends.Add(state.Region);
                }
                continue;
            }

            if (_fate.IsUnnameableUserObject(state.Region))
            {
                if (state.Written)
                {
                    kept.Add(state.Region);
                    ends.Add(state.Region);
                    _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.KEPT_USER_OBJECT, $"{name}: {state.Region}");
                }
                continue;
            }

            if (_fate.Allocations.Of(state.Region).Kind == AllocationKind.Unknown)
            {
                if (state.Written)
                    _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.KEPT_UNKNOWN_OBJECT, $"{name}: {state.Region}");
                continue;
            }
            foreach (var target in _fate.DirectStores(state.Region))
            {
                _checks.Check(GenerationStage.ReadEffects);
                Push(pending, predecessors, state.Region, target, true);
            }
            foreach (var target in _fate.DirectElementStores(state.Region))
            {
                _checks.Check(GenerationStage.ReadEffects);
                Push(pending, predecessors, state.Region, target, true);
            }
            foreach (var edge in _fate.Reachability.Edges(state.Region))
            {
                _checks.Check(GenerationStage.ReadEffects);
                Push(pending, predecessors, state.Region, edge.Target, state.Written || _fate.WasWritten(new WrittenEdge(state.Region, edge.Field, edge.Target)));
            }
        }
        AddChain(chain, Ancestors(predecessors, ends));

        var symbolic = _fate.CallInstances().Where(_fate.IsMemberEntry)
                            .SelectMany(instance => instance.Summary.Stores.Concat(instance.Summary.ReferenceStores)
                                .Where(store => store.Bases.Any(value => _fate.Matches(instance, value, keeper)))
                                .SelectMany(store => store.Values).Select(value => SymbolicName(value, instance)))
                            .Where(value => value is not null).Cast<LibraryValue>().ToArray();
        if (symbolic.Length != 0)
        {
            _symbolicKeeps[keeper] = Order(symbolic);
            chain.Add(keeper);
        }

        if (kept.Count == 0 && symbolic.Length == 0)
            return;
        _keptByRegion[keeper] = new KeeperValues(name, keeper, kept);
        _keepingChain[keeper] = chain;
    }

    private void AddChain(HashSet<string> chain, IEnumerable<string> path)
    {
        foreach (var region in path.Where(region => !_fate.Allocations.CreatedByCall(region)))
        {
            _checks.Check(GenerationStage.ReadEffects);
            chain.Add(region);
        }
    }

    /// <summary>Records one step of a keeper's walk and queues its target.</summary>
    /// <param name="pending">The walk's queue.</param>
    /// <param name="predecessors">The steps taken so far, by target.</param>
    /// <param name="from">The object the step leaves.</param>
    /// <param name="to">The object the step reaches.</param>
    /// <param name="written">Whether the call wrote the way to <paramref name="to"/>.</param>
    private static void Push(Stack<(string Region, bool Written)> pending, Dictionary<string, HashSet<string>> predecessors, string from,
                             string to, bool written)
    {
        WorkCancellation.Check();
        AddPredecessor(predecessors, from, to);
        pending.Push((to, written));
    }

    /// <summary>Records that a keeper's walk stepped from one object to another.</summary>
    /// <param name="predecessors">The steps taken so far, by target.</param>
    /// <param name="from">The object the step leaves.</param>
    /// <param name="to">The object the step reaches.</param>
    private static void AddPredecessor(Dictionary<string, HashSet<string>> predecessors, string from, string to)
    {
        if (!predecessors.TryGetValue(to, out var sources))
            predecessors[to] = sources = new HashSet<string>(StringComparer.Ordinal);
        sources.Add(from);
    }

    /// <summary>The objects on some step of a keeper's walk that leads to one of its ends, the ends included: every object on the
    /// way from the keeper to what it keeps, whichever path reached it first.</summary>
    /// <param name="predecessors">The walk's steps, by target.</param>
    /// <param name="ends">The kept objects.</param>
    private IReadOnlySet<string> Ancestors(Dictionary<string, HashSet<string>> predecessors, IEnumerable<string> ends)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(ends);
        while (pending.TryPop(out var current))
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (!reached.Add(current))
                continue;
            foreach (var source in predecessors.GetValueOrDefault(current) ?? [])
            {
                _checks.Check(GenerationStage.ReadEffects);
                pending.Push(source);
            }
        }
        return reached;
    }

    private IReadOnlySet<string> ResultRegions()
    {
        var result = _fate.StaticTargets(DriverSynthesizer.KEEP_TYPE, "R").ToHashSet(StringComparer.Ordinal);
        if (_driver.Member.MethodKind == MethodKind.Constructor)
            result.UnionWith(_fate.Heap.Regions.Keys.Where(region => _fate.Allocations.Of(region).Kind == AllocationKind.MemberObject));
        if (ObservedResultType() is not { } type || !CanHoldObject(type))
            return result;
        // A task member's result is the completion value the driver's await stores into Keep.R; any other member's is what it returns.
        if (_driver.Member.MethodKind != MethodKind.Constructor && TaskTypes.Depth(_driver.Member.ReturnType) > 0)
        {
            foreach (var instance in _fate.Heap.Instances.Values.Where(instance => instance.BodyId == ValueObservation.DriverBody(DriverSynthesizer.CALL)))
            {
                _checks.Check(GenerationStage.ReadEffects);
                foreach (var store in instance.Summary.Stores.Where(store => store.Field is
                    { IsStatic: true, Assembly: DriverSynthesizer.ASSEMBLY, ContainingType: DriverSynthesizer.KEEP_TYPE, Name: "R" }))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    result.UnionWith(ObservedValues(_fate, instance, store.Values, store.Producers));
                }
            }
            return result;
        }
        foreach (var instance in _fate.MemberInstances(DriverSynthesizer.CALL))
        {
            _checks.Check(GenerationStage.ReadEffects);
            foreach (var returned in instance.Summary.Returns)
            {
                _checks.Check(GenerationStage.ReadEffects);
                result.UnionWith(ObservedValues(_fate, instance, returned.Values, returned.Producers));
            }
        }
        return result;
    }

    /// <summary>Observes all alternatives at one entry place, preserving unresolved alternatives alongside known regions.</summary>
    /// <param name="context">The run that observed the values.</param>
    /// <param name="instance">The instance expressing the values.</param>
    /// <param name="values">Every symbolic alternative.</param>
    /// <param name="producers">Origins carried through copies and merges.</param>
    private IReadOnlySet<string> ObservedValues(RunContext context, MethodInstance instance, IEnumerable<AbstractValue> values, ValueOrigin producers)
    {
        var observed = context.ObserveValues(instance, values, producers, out var unobserved);
        if (unobserved)
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.VALUE_UNOBSERVED, $"in {instance.BodyId}");
        return observed;
    }

    private IReadOnlyDictionary<string, IReadOnlySet<string>> OutputRegions()
    {
        var outputs = new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var parameter in _driver.Member.Parameters.Where(parameter => parameter.RefKind is RefKind.Out or RefKind.Ref))
        {
            _checks.Check(GenerationStage.ReadEffects);
            var field = $"Out_{parameter.Name}_{DriverSynthesizer.CALL_VARIANT}";
            var values = _fate.StaticTargets(DriverSynthesizer.DRIVER_TYPE, field).ToHashSet(StringComparer.Ordinal);
            var initial = _fate.InitialValues(field);
            var (assigned, symbolic, changed) = AssignedValues(parameter.Ordinal);
            values.UnionWith(assigned);
            _symbolicOutputs[parameter.Name] = parameter.RefKind == RefKind.Ref && changed &&
                                               SymbolicName(new ParameterValue(parameter.Ordinal)) is { } own
                ? Order(symbolic.Append(own))
                : symbolic;
            if (parameter.RefKind == RefKind.Out)
                values.ExceptWith(initial);
            else if (!changed)
                continue;
            outputs[parameter.Name] = values;
        }
        return outputs;
    }

    private (HashSet<string> Regions, IReadOnlyList<LibraryValue> Symbolic, bool Assigned) AssignedValues(int ordinal)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        var symbolic = new List<LibraryValue>();
        var assigned = _fate.RefAssigned(ordinal);
        foreach (var (instance, parameter) in _fate.RefValues(ordinal))
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (parameter is null)
            {
                _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.OUTPUT_UNOBSERVED, $"{_driver.Member.Parameters[ordinal].Name} in {instance.BodyId}");
                continue;
            }
            foreach (var value in parameter.Values)
            {
                _checks.Check(GenerationStage.ReadEffects);
                values.UnionWith(ObservedValues(_fate, instance, [value], ValueOrigin.None));
                if (_fate.IsMemberEntry(instance) && SymbolicName(value, instance) is { } named)
                    symbolic.Add(named);
            }
        }
        return (values, Order(symbolic), assigned);
    }

    private IReadOnlyDictionary<string, IReadOnlySet<string>> StoreRegions()
    {
        var stores = new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var parameter in _driver.Member.Parameters.Where(parameter => parameter.Type is IArrayTypeSymbol))
        {
            _checks.Check(GenerationStage.ReadEffects);
            var arrays = _fate.RootsOf(new ArgumentValue(parameter.Name)).ToHashSet(StringComparer.Ordinal);
            var values = _fate.Written.Where(edge => arrays.Contains(edge.Object) && edge.Field == ELEMENT)
                                      .Select(edge => edge.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var array in arrays)
            {
                _checks.Check(GenerationStage.ReadEffects);
                values.UnionWith(_fate.DirectElementStores(array));
            }
            foreach (var instance in _fate.CallInstances())
            {
                _checks.Check(GenerationStage.ReadEffects);
                foreach (var store in instance.Summary.Elements.Concat(instance.Summary.ReferenceElementStores)
                                              .Where(store => store.Kind == ElementOperationKind.Store &&
                                                              store.Arrays.Any(value => arrays.Any(array => _fate.Matches(instance, value, array)))))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    var stored = ObservedValues(_fate, instance, store.Values, store.Producers);
                    values.UnionWith(stored);
                }
            }
            if (values.Count != 0)
                stores[parameter.Name] = values;
        }
        return stores;
    }

    private void ReadInputs()
    {
        foreach (var parameter in _driver.Parameters)
        {
            _checks.Check(GenerationStage.ReadEffects);
            // A not-run delegate is handed nothing: it carries no inputs (R5).
            if (!_fates.TryGetValue(parameter.Name, out var fate) || fate.Fate == FateClassifier.NOT_RUN)
                continue;
            if (fate.Fate == FateClassifier.HOLDER)
            {
                ReadHolderInputs(parameter);
                continue;
            }

            var variants = fate.Fate == FateClassifier.ITERATOR
                ? new[] { DriverSynthesizer.ENUMERATE_VARIANT }
                : new[] { DriverSynthesizer.CALL_VARIANT, DriverSynthesizer.ENUMERATE_VARIANT };
            var probes = parameter.Probes.Where(probe => variants.Contains(probe.Variant, StringComparer.Ordinal)).ToArray();
            AddRawInputs(parameter.Name, probes, _fate, fate.Fate == FateClassifier.INVOKE_NOW
                ? new HashSet<string>(StringComparer.Ordinal) { DriverSynthesizer.CALL }
                : null);
        }
    }

    private void ReadHolderInputs(DriverParameter parameter)
    {
        if (_confirmation is null)
            return;
        var members = _driver.Triggers.ToDictionary(trigger => trigger.Action, StringComparer.Ordinal);
        var ran = FateClassifier.TriggersThatRan(_driver, parameter, _confirmation.Run, _checks);
        foreach (var probe in parameter.Probes.Where(probe => ran.Contains(probe.Variant)))
        {
            _checks.Check(GenerationStage.ReadEffects);
            var trigger = members[probe.Variant];
            if (!_holderTriggers.TryGetValue(parameter.Name, out var fired))
                _holderTriggers[parameter.Name] = fired = new HashSet<string>(StringComparer.Ordinal);
            fired.Add(trigger.Member);
            AddRawInputs(parameter.Name, [probe], _confirmation,
                         new HashSet<string>(StringComparer.Ordinal) { trigger.Action });
        }
    }

    private void AddRawInputs(string parameter, IReadOnlyList<DriverProbe> probes, RunContext context,
                              IReadOnlySet<string>? expectedActions)
    {
        var count = probes.Select(probe => probe.InputFields.Count).DefaultIfEmpty().Max();
        for (var index = 0; index < count; index++)
        {
            _checks.Check(GenerationStage.ReadEffects);
            var regions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var probe in probes.Where(probe => probe.InputFields.Count > index))
            {
                _checks.Check(GenerationStage.ReadEffects);
                var field = probe.InputFields[index];
                var type = _driver.Compilation.GetTypeByMetadataName(DriverSynthesizer.DRIVER_TYPE)!.GetMembers(field).OfType<IFieldSymbol>().Single().Type;
                if (!CanHoldObject(type))
                    continue;
                regions.UnionWith(context.InputRegions(field, expectedActions, out var unobserved));
                if (CanHoldObject(type) && unobserved)
                    _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.DELEGATE_INPUT_UNOBSERVED, $"{parameter} input {index} through {field}");
            }
            _rawInputs.Add(new RawInput(parameter, index, regions, context, expectedActions));
        }
    }

    /// <summary>The first non-delegate parameter whose carried probe delegate ran in the member's call, or <c>null</c>.</summary>
    private string? CarriedProbeFired()
    {
        foreach (var parameter in _driver.Parameters.Where(parameter => parameter.Kind != ParameterKind.Delegate))
        {
            _checks.Check(GenerationStage.ReadEffects);
            foreach (var probe in parameter.Probes.Where(probe => probe.Variant == DriverSynthesizer.CALL_VARIANT))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (_fate.FiredExecutions(probe).Any(execution => _fate.Executions.InTree(execution, DriverSynthesizer.CALL)))
                    return parameter.Name;
            }
        }
        return null;
    }

    private LibraryValue? Name(string region, ValuePlace place, RunContext context, IReadOnlySet<string>? expectedActions)
    {
        var names = context.RootNames(region);
        if (names.Count == 1)
            return names[0];
        if (names.Count > 1)
        {
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.VALUE_SEVERAL_NAMES, $"{place}: {region} has {names.Count} names");
            return null;
        }

        if (place is not (ValuePlace.WholeResult or ValuePlace.WholeOutput or ValuePlace.WholeDelegateInput) ||
            !OnlyObservation(region, place, context) || !FreshGraph(region, place, context, expectedActions))
        {
            _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.VALUE_UNNAMED, $"{place}: {region}");
            return null;
        }
        return new NewValue();
    }

    private IReadOnlyList<LibraryValue> Names(IEnumerable<string> regions, ValuePlace place, RunContext context,
                                              IReadOnlySet<string>? expectedActions, bool markMissing = true)
    {
        var values = new List<LibraryValue>();
        foreach (var region in regions.Distinct(StringComparer.Ordinal))
        {
            _checks.Check(GenerationStage.ReadEffects);
            var roots = context.RootNames(region);
            if (roots.Count != 0)
            {
                values.AddRange(roots);
                continue;
            }
            if (place is ValuePlace.WholeResult or ValuePlace.WholeOutput or ValuePlace.WholeDelegateInput &&
                OnlyObservation(region, place, context) && FreshGraph(region, place, context, expectedActions))
            {
                values.Add(new NewValue());
                continue;
            }
            if (markMissing)
                _causes.Add(GenerationReasons.VOCABULARY, ModelCauses.VALUE_UNNAMED, $"{place}: {region}");
        }
        return Order(values);
    }

    private bool FreshGraph(string region, ValuePlace place, RunContext context, IReadOnlySet<string>? expectedActions)
    {
        if (place == ValuePlace.WholeDelegateInput && expectedActions is null)
            return false;
        var covered = new HashSet<string>(StringComparer.Ordinal);
        if (place == ValuePlace.WholeResult && _keptByRegion.TryGetValue(region, out var kept))
        {
            foreach (var value in kept.Regions.Where(value => !context.RootNames(value).Any(root => root is ThisValue)))
            {
                _checks.Check(GenerationStage.ReadEffects);
                covered.UnionWith(context.Reachability.From([value]));
            }
        }

        foreach (var reached in context.Reachability.From([region]))
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (covered.Contains(reached))
                continue;
            context.DirectStores(reached, expectedActions);
            context.DirectElementStores(reached, expectedActions);
            if (context.Unobserved is not null || context.IsProbeDelegate(reached))
                return false;
            if (context.RootNames(reached).Count != 0)
                return false;
            if (!context.IsLibraryRegion(reached) || !context.Allocations.CreatedByCall(reached))
                return false;
            var allocation = context.Allocations.Of(reached);
            if (expectedActions is not null && allocation.Kind != AllocationKind.MemberObject &&
                (allocation.Action is null || !expectedActions.Contains(allocation.Action)))
            {
                return false;
            }
            if (place == ValuePlace.WholeDelegateInput && expectedActions is not null &&
                expectedActions.Any(action => action.StartsWith('T')) && context.Allocations.CreatedByTrigger(reached) is null)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether an object, and everything a <c>new</c> place would show with it, is observed at one entry place alone (R5).</summary>
    /// <param name="region">The observed region.</param>
    /// <param name="place">The whole place it would be named at.</param>
    /// <param name="context">The run that observed it.</param>
    private bool OnlyObservation(string region, ValuePlace place, RunContext context) =>
        _observations.TryGetValue(region, out var places) && places.Count == 1 &&
        Graph(region, place, context).All(reached => !_observations.TryGetValue(reached, out var shown) || shown.SetEquals(places));

    /// <summary>The regions a place that may say <c>new</c> shows: each unnamed object with the graph it reaches.</summary>
    /// <param name="regions">The regions observed at the place.</param>
    /// <param name="context">The run that observed them.</param>
    /// <param name="place">The whole place.</param>
    private IReadOnlySet<string> Shown(IReadOnlySet<string> regions, RunContext context, ValuePlace place)
    {
        var shown = new HashSet<string>(regions, StringComparer.Ordinal);
        foreach (var region in regions.Where(region => context.RootNames(region).Count == 0))
        {
            _checks.Check(GenerationStage.ReadEffects);
            shown.UnionWith(Graph(region, place, context));
        }

        return shown;
    }

    /// <summary>What a fresh object named <c>new</c> at a whole place shows: everything it reaches, apart from what the result keeps
    /// and what that reaches, which <c>keeps.result</c> shows instead (R5).</summary>
    /// <param name="region">The fresh object.</param>
    /// <param name="place">The whole place.</param>
    /// <param name="context">The run that observed it.</param>
    private IEnumerable<string> Graph(string region, ValuePlace place, RunContext context)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        if (place == ValuePlace.WholeResult && _keptByRegion.TryGetValue(region, out var kept))
            covered.UnionWith(context.Reachability.From(kept.Regions));
        return _stage.Each(context.Reachability.From([region])).Where(reached => !covered.Contains(reached));
    }

    private void Observe(IEnumerable<string> regions, string place)
    {
        foreach (var region in regions)
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (!_observations.TryGetValue(region, out var places))
                _observations[region] = places = new HashSet<string>(StringComparer.Ordinal);
            places.Add(place);
        }
    }

    private static IReadOnlyList<LibraryValue> Order(IEnumerable<LibraryValue> values) =>
        values.GroupBy(value => value.Canonical, StringComparer.Ordinal).Select(group => group.First())
              .OrderBy(value => value.Canonical, StringComparer.Ordinal).ToArray();

    private static IReadOnlySet<string>? ExpectedAction(ValuePlace place) => place switch
    {
        ValuePlace.WholeResult or ValuePlace.WholeOutput => new HashSet<string>(StringComparer.Ordinal) { DriverSynthesizer.CALL },
        _ => null
    };

    private static bool CanHoldObject(ITypeSymbol type) => TypeShape.Of(type) is
        TypeShapeKind.Delegate or TypeShapeKind.TaskOfT or TypeShapeKind.StructWithReferences or TypeShapeKind.Reference;

    private static bool IsSequence(ITypeSymbol type) => type.SpecialType == SpecialType.System_Collections_IEnumerable ||
        type.AllInterfaces.Any(candidate => candidate.SpecialType == SpecialType.System_Collections_IEnumerable);

    private sealed record RawInput(string Parameter, int Index, IReadOnlySet<string> Regions, RunContext Context,
                                   IReadOnlySet<string>? ExpectedActions);

    private sealed record KeeperValues(string Name, string Region, IReadOnlySet<string> Regions);

    private sealed record WrittenEdge(string Object, string Field, string Value);

    /// <summary>Finds the arrays whose cells a reference names through source-call bindings and reference returns.</summary>
    /// <param name="heap">The solved heap.</param>
    /// <param name="instance">The instance expressing the reference.</param>
    /// <param name="target">The reference location.</param>
    /// <param name="unobserved">Whether any binding has no proven reference location.</param>
    internal static IReadOnlySet<string> ReferenceArrays(HeapSolution heap, MethodInstance instance, ReferenceTarget target, out bool unobserved)
    {
        WorkCancellation.Check();
        unobserved = false;
        return ReferenceArrays(heap, instance, target, fields: false, [], ref unobserved);
    }

    /// <summary>Finds every object a reference's storage lies in: the arrays whose cells it names, as
    /// <see cref="ReferenceArrays(HeapSolution, MethodInstance, ReferenceTarget, out bool)"/> does, and the objects whose field it
    /// names.</summary>
    /// <param name="heap">The solved heap.</param>
    /// <param name="instance">The instance expressing the reference.</param>
    /// <param name="target">The reference location.</param>
    /// <param name="unobserved">Whether any binding has no proven reference location.</param>
    internal static IReadOnlySet<string> ReferenceStorage(HeapSolution heap, MethodInstance instance, ReferenceTarget target, out bool unobserved)
    {
        unobserved = false;
        return ReferenceArrays(heap, instance, target, fields: true, [], ref unobserved);
    }

    /// <summary>Finds every field a reference names, as <see cref="ReferenceStorage"/> follows it: each the object the field lies in — a
    /// static field in the static storage of its declaring type —, the slot of it — through every struct field when the field is in a
    /// struct's storage (R1), including a collection cell — and its leaf field. EffectReader.ReferenceFieldStores and its handoff
    /// StoreTargets, and DirectStores consume this projection; ReferenceStorage and ReferenceArrays share its binding walk.</summary>
    /// <param name="heap">The solved heap.</param>
    /// <param name="instance">The instance expressing the reference.</param>
    /// <param name="target">The reference location.</param>
    internal static IReadOnlyList<(string Region, string Slot, IrFieldRef Field)> ReferenceFields(HeapSolution heap, MethodInstance instance,
                                                                                                 ReferenceTarget target)
    {
        var unobserved = false;
        var places = new List<(string Region, string Slot, IrFieldRef Field)>();
        ReferenceArrays(heap, instance, target, fields: true, [], ref unobserved, places);
        return places.Distinct().ToArray();
    }

    /// <summary>Finds every static field a reference names, through the same bindings <see cref="ReferenceStorage"/> follows: the field
    /// the place lies in, the first of its path, whether or not the heap has a region of its static storage — a reference to a scalar
    /// static that nothing loads or stores has none. A cell of what a static field holds is no place of the static.</summary>
    /// <param name="heap">The solved heap.</param>
    /// <param name="instance">The instance expressing the reference.</param>
    /// <param name="target">The reference location.</param>
    internal static IReadOnlyList<IrFieldRef> ReferenceStatics(HeapSolution heap, MethodInstance instance, ReferenceTarget target)
    {
        var unobserved = false;
        var statics = new List<IrFieldRef>();
        ReferenceArrays(heap, instance, target, fields: true, [], ref unobserved, statics: statics);
        return statics.Distinct().ToArray();
    }

    /// <summary>Follows each reference binding once on its path to an array cell, or to a field when <paramref name="fields"/>.</summary>
    /// <param name="heap">The solved heap.</param>
    /// <param name="instance">The instance expressing the reference.</param>
    /// <param name="target">The reference location.</param>
    /// <param name="fields">Whether a reference to a field names the object holding it.</param>
    /// <param name="seen">Bindings already visited.</param>
    /// <param name="unobserved">Whether any binding has no proven reference location.</param>
    /// <param name="places">Where each field a reference names is added, with the object it lies in and its slot; <c>null</c> for none.</param>
    /// <param name="statics">Where each static field a reference names a place of is added; <c>null</c> for none.</param>
    private static IReadOnlySet<string> ReferenceArrays(HeapSolution heap, MethodInstance instance, ReferenceTarget target, bool fields,
                                                        HashSet<(string Instance, ReferenceTarget Target)> seen, ref bool unobserved,
                                                        List<(string Region, string Slot, IrFieldRef Field)>? places = null,
                                                        List<IrFieldRef>? statics = null)
    {
        WorkCancellation.Check();
        if (!seen.Add((instance.Id, target)))
            return new HashSet<string>(StringComparer.Ordinal);
        switch (target)
        {
            case ReferenceParameterElement element:
                return instance.Parameters.GetValueOrDefault(element.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            // A field keeps its full slot and leaf, whether it lies directly in a holder or below an array cell (R4).
            case ReferenceCell cell when cell.IsOnCollection || fields:
            {
                if (cell is { Field.IsStatic: true, IsOnCollection: false })
                    statics?.Add(cell.Field);
                // A static field lies in the static storage of its declaring type, whatever construction of it.
                var holders = cell.Field.IsStatic
                    ? heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Static && region.TypeKey is { } type &&
                                                          FieldSlot.WithoutTypeArguments(type) == FieldSlot.TypeKey(cell.Field.Assembly, cell.Field.ContainingTypeId))
                          .Select(region => region.Identity)
                    : cell.Bases.SelectMany(value => heap.Resolve(instance.Id, value));
                var bases = (cell.IsOnCollection ? holders.SelectMany(region => heap.PointsTo(region, FieldSlot.Key(cell.Field))) : holders)
                            .ToHashSet(StringComparer.Ordinal);
                if (places is not null && (!cell.IsOnCollection || cell.StructPath.Count != 0))
                {
                    var field = cell.StructPath.LastOrDefault() ?? cell.Field;
                    places.AddRange(bases.Select(region => heap.Located(region, cell.Slot)).OfType<(string Region, string Slot)>()
                                         .Select(place => (place.Region, place.Slot, field)));
                }
                return cell.IsOnCollection ? bases
                    : bases.Select(region => heap.Located(region, "")?.Region).OfType<string>().ToHashSet(StringComparer.Ordinal);
            }
            case ReferenceParameter parameter:
            {
                var arrays = new HashSet<string>(StringComparer.Ordinal);
                foreach (var edge in heap.Edges.Where(edge => edge.CalleeInstance == instance.Id))
                {
                    WorkCancellation.Check();
                    var caller = heap.Instances[edge.CallerInstance];
                    foreach (var argument in caller.Summary.Calls.Where(call => call.OperationId == edge.OperationId).SelectMany(call => call.Arguments)
                                                   .Where(argument => argument.ParameterOrdinal == parameter.Ordinal))
                    {
                        WorkCancellation.Check();
                        if (argument.References.Count == 0)
                        {
                            foreach (var path in argument.Values.OfType<PathValue>().Where(path => path.Segments.LastOrDefault() == ELEMENT))
                            {
                                WorkCancellation.Check();
                                var storage = path.Segments.Count == 1 ? path.Base : new PathValue(path.Base, path.Segments.Take(path.Segments.Count - 1).ToArray());
                                var regions = heap.Resolve(caller.Id, storage);
                                unobserved |= regions.Count == 0;
                                arrays.UnionWith(regions);
                            }
                        }
                        foreach (var reference in argument.References)
                        {
                            WorkCancellation.Check();
                            arrays.UnionWith(ReferenceArrays(heap, caller, reference, fields, seen, ref unobserved, places, statics));
                        }
                    }
                }
                return arrays;
            }
            case ReferenceCall call:
            {
                var arrays = new HashSet<string>(StringComparer.Ordinal);
                var callees = heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == call.OperationId)
                                  .Select(edge => heap.Instances[edge.CalleeInstance]).ToArray();
                if (callees.Length == 0)
                    unobserved = true;
                foreach (var callee in callees)
                {
                    WorkCancellation.Check();
                    if (callee.Summary.ReferenceReturns.Count == 0)
                        unobserved = true;
                    foreach (var reference in callee.Summary.ReferenceReturns)
                    {
                        WorkCancellation.Check();
                        arrays.UnionWith(ReferenceArrays(heap, callee, reference, fields, seen, ref unobserved, places, statics));
                    }
                }
                return arrays;
            }
            case ReferenceCallCollection collection:
            {
                var arrays = heap.Resolve(instance.Id, new CallResultValue(collection.OperationId));
                unobserved |= arrays.Count == 0;
                return arrays;
            }
            case ReferenceUnproven:
                unobserved = true;
                return new HashSet<string>(StringComparer.Ordinal);
            default:
                return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private sealed class RunContext
    {
        private readonly Driver _driver;
        private readonly Dictionary<string, List<LibraryValue>> _roots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _fired = new(StringComparer.Ordinal);
        private readonly IReadOnlySet<WrittenEdge> _placed;
        private readonly IReadOnlySet<WrittenEdge> _placedSeeds;
        private readonly GenerationChecks _checks;
        private readonly StageChecks _stage;
        private readonly Dictionary<string, StoreIndex> _actionStores = new(StringComparer.Ordinal);
        private IReadOnlyList<MethodInstance>? _callInstances;
        private StoreIndex? _callStores;
        private IReadOnlyList<MemberStore>? _memberStores;

        /// <summary>One run as value provenance reads it: its roots, the edges its stores wrote and the edges setup placed. Each of
        /// those scans checks at the boundary of its repeated work, and so do the reachability, the allocations and the observation it
        /// owns.</summary>
        /// <param name="driver">The driver.</param>
        /// <param name="run">The run.</param>
        /// <param name="checks">The generation's checks, which cancel the scans.</param>
        public RunContext(Driver driver, ScopeRun run, GenerationChecks checks)
        {
            _checks = checks;
            _stage = new StageChecks(checks, GenerationStage.ReadEffects);
            _driver = driver;
            Run = run;
            Heap = run.Heap!;
            Analysis = run.Executions!;
            Observation = new ValueObservation(driver, run, _stage.Each(GenerationHandoffs.UnfollowedCalls(run))
                                                                  .Select(call => (call.Instance.Id, call.OperationId)).ToHashSet(), _stage);
            Reachability = new HeapReachability(Heap, null, _stage);
            var result = Reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.KEEP_TYPE, "R"));
            Executions = new DriverExecutions(driver, Analysis, Reachability.From(result is null ? [] : Reachability.Targets(result)));
            Allocations = new Allocations(driver, Heap, Analysis, Executions, Reachability, _stage);
            Written = ReadWrittenEdges();
            _placed = ReadPlacedEdges();
            _placedSeeds = ReadPlacedSeeds();
            BuildRoots();
            BuildFired();
        }

        public ScopeRun Run { get; }
        public HeapSolution Heap { get; }
        public ExecutionAnalysis Analysis { get; }
        public ValueObservation Observation { get; }
        public HeapReachability Reachability { get; }
        public DriverExecutions Executions { get; }
        public Allocations Allocations { get; }
        public IReadOnlySet<WrittenEdge> Written { get; }

        /// <summary>The first value this run observed that may come from somewhere the analysis does not follow, or <c>null</c>.</summary>
        public GenerationCause? Unobserved { get; private set; }

        public IReadOnlySet<string> StaticTargets(string type, string field) =>
            Reachability.Held(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, type, field));

        /// <summary>The values setup placed in one argument's initial output slot, before additive call writes.</summary>
        /// <param name="field">The driver's output slot.</param>
        public IReadOnlySet<string> InitialValues(string field) => Heap.Instances.Values
            .Where(instance => (Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>()).Any(Executions.InSetup))
            .SelectMany(instance => instance.Summary.Stores.Where(store => store.Field is
                { IsStatic: true, Assembly: DriverSynthesizer.ASSEMBLY, ContainingType: DriverSynthesizer.DRIVER_TYPE } && store.Field.Name == field)
                                           .SelectMany(store => store.Values).SelectMany(value => Resolve(instance, value)))
            .Where(Allocations.CreatedInSetup).ToHashSet(StringComparer.Ordinal);

        public IReadOnlyList<LibraryValue> RootNames(string region)
        {
            var roots = _roots.GetValueOrDefault(region)?.ToList() ?? [];
            if (roots.Count == 0 && Heap.Regions.TryGetValue(region, out var target))
            {
                roots.AddRange(_roots.Where(pair => Heap.Regions.TryGetValue(pair.Key, out var candidate) && candidate.Group == target.Group)
                                     .SelectMany(pair => pair.Value));
            }
            if (roots.Count == 0)
                return [];
            var depth = roots.Min(ElementDepth);
            return Order(roots.Where(root => ElementDepth(root) == depth));
        }

        public IReadOnlySet<string> RootsOf(LibraryValue value) =>
            _roots.Where(pair => pair.Value.Any(candidate => candidate.Canonical == value.Canonical))
                  .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);

        // bounded: forwards once to the static ActionOf, without re-entering this method.
        public string? ActionOf(string execution) => ValueProvenance.ActionOf(Executions, execution);

        public IReadOnlySet<string> FiredExecutions(DriverProbe probe) =>
            _fired.GetValueOrDefault(probe.FiredField) ?? new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The objects an executed probe received, including values forwarded through source calls; records any input the heap cannot carry.</summary>
        /// <param name="field">The probe's input observation field.</param>
        /// <param name="expectedActions">The actions whose inputs are observed, or <c>null</c> for the member's call and enumeration.</param>
        /// <param name="unobserved">Whether a received value had no observed object.</param>
        public IReadOnlySet<string> InputRegions(string field, IReadOnlySet<string>? expectedActions, out bool unobserved)
        {
            var regions = StaticTargets(DriverSynthesizer.DRIVER_TYPE, field).ToHashSet(StringComparer.Ordinal);
            unobserved = false;
            var incoming = Heap.Edges.ToLookup(edge => edge.CalleeInstance);
            foreach (var instance in Heap.Instances.Values)
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (!(Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>(StringComparer.Ordinal))
                             .Any(execution => ActionOf(execution) is { } action && (expectedActions is null || expectedActions.Contains(action))))
                {
                    continue;
                }
                var ordinals = instance.Summary.Stores.Where(store => store.Field is
                    { IsStatic: true, Assembly: DriverSynthesizer.ASSEMBLY, ContainingType: DriverSynthesizer.DRIVER_TYPE } && store.Field.Name == field)
                                       .SelectMany(store => store.Producers.Parameters).ToHashSet();
                if (ordinals.Count == 0)
                    continue;
                foreach (var edge in incoming[instance.Id])
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    var caller = Heap.Instances[edge.CallerInstance];
                    foreach (var argument in caller.Summary.Calls.Where(call => call.OperationId == edge.OperationId)
                                                   .SelectMany(call => call.Arguments).Where(argument => ordinals.Contains(argument.ParameterOrdinal)))
                    {
                        _checks.Check(GenerationStage.ReadEffects);
                        var observed = ObserveValues(caller, argument.Values, argument.Producers, out var missing);
                        regions.UnionWith(observed);
                        unobserved |= missing;
                    }
                }
            }
            return regions;
        }

        /// <summary>Whether a store in the member's call or enumeration tree writes this parameter's reference location.</summary>
        /// <param name="ordinal">The member's parameter ordinal.</param>
        public bool RefAssigned(int ordinal)
        {
            var roots = MemberInstances(DriverSynthesizer.CALL).Concat(MemberInstances(DriverSynthesizer.ENUMERATE))
                                                              .Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
            return Heap.Instances.Values.Where(instance =>
                (Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>(StringComparer.Ordinal))
                         .Any(execution => Executions.InTree(execution, DriverSynthesizer.CALL) ||
                                           Executions.InTree(execution, DriverSynthesizer.ENUMERATE)))
                       .Any(instance => instance.Summary.ReferenceAccesses.Where(access => access.Kind == SummaryAccessKind.Store)
                                                .SelectMany(access => access.Targets)
                                                .Any(target => RefersToParameter(instance, target, ordinal, roots, [])));
        }

        /// <summary>The values carried by reference parameters bound to this member's output location throughout its call tree.</summary>
        /// <param name="ordinal">The member's parameter ordinal.</param>
        public IEnumerable<(MethodInstance Instance, RefParameterTransfer? Parameter)> RefValues(int ordinal)
        {
            var roots = MemberInstances(DriverSynthesizer.CALL).Concat(MemberInstances(DriverSynthesizer.ENUMERATE))
                                                              .Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var instance in Heap.Instances.Values.Where(instance =>
                (Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>(StringComparer.Ordinal))
                         .Any(execution => Executions.InTree(execution, DriverSynthesizer.CALL) ||
                                           Executions.InTree(execution, DriverSynthesizer.ENUMERATE))))
            {
                _checks.Check(GenerationStage.ReadEffects);
                foreach (var parameter in instance.Summary.RefParameters.Where(parameter =>
                    RefersToParameter(instance, new ReferenceParameter(parameter.Ordinal), ordinal, roots, [])))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    yield return (instance, parameter);
                }
                if (!Run.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
                    continue;
                foreach (var access in instance.Summary.ReferenceAccesses.Where(access => access.Kind == SummaryAccessKind.Store &&
                    access.Targets.Any(target => RefersToParameter(instance, target, ordinal, roots, []))))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    var store = body.Blocks.SelectMany(block => block.Operations).OfType<IrStoreReferenceOperation>()
                                    .FirstOrDefault(store => store.Id == access.OperationId);
                    var symbol = store is null ? null : body.Values[store.Value].SymbolKey;
                    var variable = symbol is null ? null : instance.Summary.Variables.FirstOrDefault(variable => variable.SymbolKey == symbol);
                    if (variable is not null && !variable.Values.Any(value => Resolve(instance, value).Count == 0 &&
                        !(value is ParameterValue parameter && (instance.BodyId == IrLowering.RootBodyId(_driver.Member) ||
                            RefersToParameter(instance, new ReferenceParameter(parameter.Ordinal), ordinal, roots, [])))))
                        yield return (instance, new RefParameterTransfer(ordinal, variable.Values));
                    else if (variable is not null || access.Targets.Any(target => target is ReferenceCall))
                        yield return (instance, null);
                }
            }
        }

        /// <summary>Follows a reference's source-call bindings and reference returns to the member's parameter location.</summary>
        /// <param name="instance">The instance in which the reference is expressed.</param>
        /// <param name="target">The reference target.</param>
        /// <param name="ordinal">The member's parameter ordinal.</param>
        /// <param name="roots">The member instances in the call and enumeration trees.</param>
        /// <param name="seen">Reference bindings already visited.</param>
        private bool RefersToParameter(MethodInstance instance, ReferenceTarget target, int ordinal, IReadOnlySet<string> roots,
                                        HashSet<(string Instance, ReferenceTarget Target)> seen)
        {
            _checks.Check(GenerationStage.ReadEffects);
            if (!seen.Add((instance.Id, target)))
                return false;
            if (target is ReferenceParameter parameter)
            {
                if (roots.Contains(instance.Id))
                    return parameter.Ordinal == ordinal;
                return Heap.Edges.Where(edge => edge.CalleeInstance == instance.Id).Any(edge =>
                {
                    var caller = Heap.Instances[edge.CallerInstance];
                    return caller.Summary.Calls.Where(call => call.OperationId == edge.OperationId)
                                 .SelectMany(call => call.Arguments).Where(argument => argument.ParameterOrdinal == parameter.Ordinal)
                                 .SelectMany(argument => argument.References)
                                 .Any(reference => RefersToParameter(caller, reference, ordinal, roots, seen));
                });
            }
            return target is ReferenceCall call &&
                   Heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == call.OperationId)
                       .Any(edge => Heap.Instances[edge.CalleeInstance] is { } callee &&
                                    callee.Summary.ReferenceReturns.Any(reference => RefersToParameter(callee, reference, ordinal, roots, seen)));
        }

        public bool WasWritten(WrittenEdge edge)
        {
            if (IsPlacedSlotSeed(edge.Object, edge.Field, edge.Value))
                return false;
            if (Written.Contains(edge))
                return true;
            foreach (var store in _memberStores ??= ReadMemberStores())
            {
                _checks.Check(GenerationStage.ReadEffects);
                if ((FieldMatches(edge.Field, store.Field) && MatchesAny(store.Bases, edge.Object) ||
                     store.Places.Any(place => place.Object == edge.Object && FieldMatches(edge.Field, place.Field))) &&
                    MatchesAny(store.Values, edge.Value))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The stores of the member's own call and enumeration entries, each read once for <see cref="WasWritten"/>: a field
        /// store with what its bases and values stand for and the places it writes, and a cell store with its slot and the regions its
        /// arrays and values resolve to.</summary>
        private IReadOnlyList<MemberStore> ReadMemberStores()
        {
            var stores = new List<MemberStore>();
            foreach (var instance in MemberInstances(DriverSynthesizer.CALL).Concat(MemberInstances(DriverSynthesizer.ENUMERATE)))
            {
                _checks.Check(GenerationStage.ReadEffects);
                foreach (var store in instance.Summary.Stores.Concat(instance.Summary.ReferenceStores))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    stores.Add(new MemberStore(store.Field.Name, MatchOf(instance, store.Bases), Places(instance, store.Bases, store.Field).ToArray(),
                                               MatchOf(instance, store.Values)));
                }
                foreach (var store in instance.Summary.Elements.Concat(instance.Summary.ReferenceElementStores).Where(store => store.Kind == ElementOperationKind.Store))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    // A cell store matches its arrays and values by what they resolve to, never by a name.
                    stores.Add(new MemberStore(store.Slot, new ValueMatch(store.Arrays.SelectMany(value => Resolve(instance, value)).ToHashSet(StringComparer.Ordinal), []),
                                               [], new ValueMatch(store.Values.SelectMany(value => Resolve(instance, value)).ToHashSet(StringComparer.Ordinal), [])));
                }
            }
            return stores;
        }

        /// <summary>The complete observations of values stored into an object's fields, including fields a helper's reference names
        /// through ReferenceFields, with the same stored-value observation DirectElementStores uses.</summary>
        /// <param name="object">The object region.</param>
        /// <param name="actions">The observed actions, or null for the member's call and enumeration.</param>
        public IReadOnlySet<string> DirectStores(string @object, IReadOnlySet<string>? actions = null)
        {
            var values = new HashSet<string>(StringComparer.Ordinal);
            GenerationCause? unobserved = null;
            foreach (var (_, store) in StoresOf(actions).Fields.Of(@object))
            {
                _checks.Check(GenerationStage.ReadEffects);
                var slots = store.Places.Where(place => place.Object == @object).Select(place => place.Field).ToArray();
                if (slots.Length == 0 && store.Bases is { } bases && MatchesAny(bases, @object))
                    slots = [store.Field];
                if (slots.Length == 0)
                    continue;
                var stored = store.Stored.Value;
                unobserved ??= stored.Unobserved;
                foreach (var slot in slots)
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    values.UnionWith(stored.Values.Where(value => !IsPlacedSlotSeed(@object, slot, value)));
                }
            }
            Unobserved ??= unobserved;
            return values;
        }

        /// <summary>The objects stored values put into a slot: a struct copy is no object, and storing it puts there what its reference
        /// fields hold (R2).</summary>
        /// <param name="values">The stored values.</param>
        private IEnumerable<string> Held(IEnumerable<string> values) =>
            values.SelectMany(value => Reachability.IsCopy(value) ? Reachability.Edges(value).Select(edge => edge.Target) : [value]);

        /// <summary>Whether a value stored into a field of an object is the delegate seed setup placed in that same field of that
        /// same object: a call that stores it back — a combination with the slot's own value, a removal from it — writes nothing
        /// new into the slot (task 11, rule (b)).</summary>
        /// <param name="object">The object stored into.</param>
        /// <param name="field">The field stored into.</param>
        /// <param name="value">The stored object.</param>
        public bool IsPlacedSlotSeed(string @object, string field, string value) =>
            _placedSeeds.Any(edge => edge.Object == @object && edge.Value == value && FieldMatches(edge.Field, field));

        private bool FieldCarriesObject(IrFieldRef field) =>
            FieldSymbols.Of(field, _driver.Compilation) is not IFieldSymbol symbol || CanHoldObject(symbol.Type);

        /// <summary>The observed objects a reference store writes, shared by DirectStores and DirectElementStores. The source variable
        /// supplies its values; a parameter transfer supplies a value with no variable name. An unobserved source is refused: the
        /// first cause its observation finds comes back with the objects, for the reader to record.</summary>
        /// <param name="instance">The instance storing through the reference.</param>
        /// <param name="access">The reference store.</param>
        private StoredValues ReferenceStoredValues(MethodInstance instance, SummaryReferenceAccess access)
        {
            var values = new HashSet<string>(StringComparer.Ordinal);
            GenerationCause? unobserved = null;
            if (!Run.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
                return new StoredValues([], null);
            var store = body.Blocks.SelectMany(block => block.Operations).OfType<IrStoreReferenceOperation>()
                            .FirstOrDefault(store => store.Id == access.OperationId);
            if (store is null)
                return new StoredValues([], null);
            var symbol = body.Values[store.Value].SymbolKey;
            var variable = symbol is null ? null : instance.Summary.Variables.FirstOrDefault(variable => variable.SymbolKey == symbol);
            if (variable is not null)
                values.UnionWith(Held(ObserveUnrecorded(instance, variable.Values, ValueOrigin.None, out unobserved)));
            else
            {
                var transfers = instance.Summary.RefParameters.Where(parameter => access.Targets.Contains(new ReferenceParameter(parameter.Ordinal))).ToArray();
                foreach (var parameter in transfers)
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    values.UnionWith(Held(ObserveUnrecorded(instance, parameter.Values, ValueOrigin.None, out var missing)));
                    unobserved ??= missing;
                }
                if (transfers.Length == 0)
                {
                    unobserved = new GenerationCause(GenerationReasons.VOCABULARY, ModelCauses.REFERENCE_VALUE_UNOBSERVED,
                                                     $"no proven value in {instance.BodyId}#{access.OperationId}");
                }
            }
            return new StoredValues(values.ToArray(), unobserved);
        }

        /// <summary>The regions stored directly into cells of an array by the member.</summary>
        /// <param name="object">The array region.</param>
        /// <param name="actions">The observed actions, or null for the member's call and enumeration.</param>
        public IReadOnlySet<string> DirectElementStores(string @object, IReadOnlySet<string>? actions = null)
        {
            var values = new HashSet<string>(StringComparer.Ordinal);
            var index = StoresOf(actions);
            GenerationCause? unobserved = null;
            foreach (var (position, store) in index.Cells.Of(@object))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (!MatchesAny(store.Arrays, @object))
                    continue;
                // A store through a reference with no proven place is met, whatever the object, before every store that follows it.
                if (index.Unproven is { } unproven && unproven.Position <= position)
                    unobserved ??= unproven.Cause;
                var stored = store.Stored.Value;
                unobserved ??= stored.Unobserved;
                values.UnionWith(stored.Values);
            }
            Unobserved ??= unobserved ?? index.Unproven?.Cause;
            return values;
        }

        /// <summary>The stores of the instances that run the observed actions, read once for each set of actions.</summary>
        /// <param name="actions">The observed actions, or null for the member's call and enumeration.</param>
        private StoreIndex StoresOf(IReadOnlySet<string>? actions)
        {
            if (actions is null)
                return _callStores ??= IndexStores(StoreInstances(null));
            var key = string.Join('\n', actions.Order(StringComparer.Ordinal));
            if (!_actionStores.TryGetValue(key, out var index))
                _actionStores[key] = index = IndexStores(StoreInstances(actions));
            return index;
        }

        /// <summary>Reads the stores of a set of instances once, each at its position in the scan DirectStores and DirectElementStores
        /// make of the instances, kept by the objects it may write.</summary>
        /// <param name="instances">The instances, in the order of the scan.</param>
        private StoreIndex IndexStores(IEnumerable<MethodInstance> instances)
        {
            var fields = new StoreTable<FieldWrite>();
            var cells = new StoreTable<CellWrite>();
            (int Position, GenerationCause Cause)? unproven = null;
            foreach (var instance in instances)
            {
                _checks.Check(GenerationStage.ReadEffects);
                foreach (var store in _stage.Each(instance.Summary.Stores.Concat(instance.Summary.ReferenceStores)).Where(store => FieldCarriesObject(store.Field)))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    // A store into a struct's storage the object keeps writes the object, at the slot path through the struct (R1): at the
                    // first place of each object.
                    var places = _stage.Each(Places(instance, store.Bases, store.Field)).DistinctBy(place => place.Object, StringComparer.Ordinal).ToArray();
                    var bases = MatchOf(instance, store.Bases);
                    fields.Append(new FieldWrite(places, bases, store.Field.Name, Stored(instance, store.Values, store.Producers)),
                                  places.Select(place => place.Object).Concat(bases.Regions), bases.Named.Count != 0);
                }
                foreach (var store in _stage.Each(instance.Summary.Elements.Concat(instance.Summary.ReferenceElementStores))
                                            .Where(store => store.Kind == ElementOperationKind.Store))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    var arrays = MatchOf(instance, store.Arrays);
                    cells.Append(new CellWrite(arrays, Stored(instance, store.Values, store.Producers)), arrays.Regions, arrays.Named.Count != 0);
                }
                foreach (var access in _stage.Each(instance.Summary.ReferenceAccesses).Where(access => access.Kind == SummaryAccessKind.Store))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    var stored = new Lazy<StoredValues>(() => ReferenceStoredValues(instance, access), LazyThreadSafetyMode.None);
                    if (instance.Summary.ReferenceStores.All(store => store.OperationId != access.OperationId))
                    {
                        var places = _stage.Each(access.Targets).SelectMany(target => ReferenceFields(Heap, instance, target))
                                           .Where(place => FieldCarriesObject(place.Field)).Select(place => (place.Region, place.Slot)).ToArray();
                        fields.Append(new FieldWrite(places, null, "", stored), places.Select(place => place.Region), false);
                    }
                    var arrays = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var target in _stage.Each(access.Targets))
                    {
                        _checks.Check(GenerationStage.ReadEffects);
                        arrays.UnionWith(ReferenceArrays(Heap, instance, target, out var unobserved));
                        if (unobserved && unproven is null)
                        {
                            unproven = (cells.Count, new GenerationCause(GenerationReasons.VOCABULARY, ModelCauses.REFERENCE_VALUE_UNOBSERVED,
                                                                         $"no proven place in {instance.BodyId}#{access.OperationId}"));
                        }
                    }
                    cells.Append(new CellWrite(new ValueMatch(arrays, []), stored), arrays, false);
                }
            }
            return new StoreIndex(fields, cells, unproven);
        }

        /// <summary>What a store of values puts into the objects it writes, observed on first use: the objects the values hold, and what
        /// a struct copy among them holds in its stead (R2).</summary>
        /// <param name="instance">The instance storing.</param>
        /// <param name="values">The stored values.</param>
        /// <param name="producers">Origins preserved through copies and merges.</param>
        private Lazy<StoredValues> Stored(MethodInstance instance, IEnumerable<AbstractValue> values, ValueOrigin producers) =>
            new(() => new StoredValues(Held(ObserveUnrecorded(instance, values, producers, out var unobserved)).ToArray(), unobserved),
                LazyThreadSafetyMode.None);

        private IEnumerable<MethodInstance> StoreInstances(IReadOnlySet<string>? actions) => actions is null ? CallInstances() :
            _stage.Each(Heap.Instances.Values).Where(instance => IsLibraryBody(instance.BodyId) &&
                (Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>(StringComparer.Ordinal))
                             .Any(execution => ActionOf(execution) is { } action && actions.Contains(action)));

        // bounded: delegates once to the wrapped value, without re-entering this method.
        public bool IsMemberEntry(MethodInstance instance) => Observation.IsMemberEntry(instance);

        /// <summary>The instances of library bodies that run in the member's call or enumeration, found on first use.</summary>
        public IEnumerable<MethodInstance> CallInstances() => _stage.Each(_callInstances ??= _stage.Each(Heap.Instances.Values).Where(instance =>
            IsLibraryBody(instance.BodyId) && (Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>(StringComparer.Ordinal))
                                                  .Any(execution => ActionOf(execution) is DriverSynthesizer.CALL or DriverSynthesizer.ENUMERATE)).ToArray());

        public bool Matches(MethodInstance instance, AbstractValue value, string region)
        {
            if (Resolve(instance, value).Contains(region))
                return true;
            if (!IsMemberEntry(instance))
                return false;
            return MatchesByName(value, region);
        }

        /// <summary>Whether a value of the member's own entry stands for a region by the region's name: the receiver for the receiver
        /// or an object the member made, a parameter for its argument.</summary>
        /// <param name="value">The value.</param>
        /// <param name="region">The region.</param>
        private bool MatchesByName(AbstractValue value, string region) => value switch
        {
            ConcurrencyHunter.Heap.ThisValue => RootNames(region).Any(root => root is ThisValue) ||
                                                Allocations.Of(region).Kind == AllocationKind.MemberObject,
            ParameterValue parameter when parameter.Ordinal >= 0 && parameter.Ordinal < _driver.Member.Parameters.Length =>
                RootNames(region).Any(root => root is ArgumentValue argument &&
                                              argument.Parameter == _driver.Member.Parameters[parameter.Ordinal].Name),
            _ => false
        };

        /// <summary>What a set of values of one instance stands for, read once: <see cref="Matches"/> of any of them against a region
        /// is <see cref="MatchesAny"/> of this.</summary>
        /// <param name="instance">The instance expressing the values.</param>
        /// <param name="values">The values.</param>
        private ValueMatch MatchOf(MethodInstance instance, IEnumerable<AbstractValue> values)
        {
            var regions = new HashSet<string>(StringComparer.Ordinal);
            var named = new List<AbstractValue>();
            var entry = IsMemberEntry(instance);
            foreach (var value in values)
            {
                _checks.Check(GenerationStage.ReadEffects);
                regions.UnionWith(Resolve(instance, value));
                if (entry)
                    named.Add(value);
            }
            return new ValueMatch(regions, named);
        }

        /// <summary>Whether any of a set of values stands for a region, as <see cref="Matches"/> answers it for each of them.</summary>
        /// <param name="match">What the values stand for.</param>
        /// <param name="region">The region.</param>
        private bool MatchesAny(ValueMatch match, string region) =>
            match.Regions.Contains(region) || match.Named.Any(value => MatchesByName(value, region));

        public IReadOnlyList<MethodInstance> MemberInstances(string action)
        {
            var body = IrLowering.RootBodyId(_driver.Member);
            return Heap.Instances.Values.Where(instance => instance.BodyId == body && IsMemberEntry(instance) &&
                (Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>(StringComparer.Ordinal))
                         .Any(execution => Executions.InTree(execution, action))).ToArray();
        }

        public bool IsProbeDelegate(string region) => Allocations.Of(region).Role == DriverRole.ProbeDelegate;

        public bool IsLibraryRegion(string region) => Heap.Regions.TryGetValue(region, out var value) && value.Kind != HeapRegionKind.Static &&
            value.TypeKey is { } type && !type.StartsWith($"{DriverSynthesizer.ASSEMBLY}:", StringComparison.Ordinal);

        public bool IsUnnameableUserObject(string region) => Heap.Regions.TryGetValue(region, out var value) &&
            value.TypeKey?.StartsWith($"{DriverSynthesizer.ASSEMBLY}:", StringComparison.Ordinal) == true && !IsProbeDelegate(region);

        public IReadOnlySet<string> Yields(string region)
        {
            var values = new HashSet<string>(StringComparer.Ordinal);
            if (Heap.LibrarySequences.TryGetValue(region, out var sequence))
                values.UnionWith(sequence.Yields);
            foreach (var iterator in Heap.IteratorObjects.Where(iterator => iterator.RegionId == region))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (!Heap.Instances.TryGetValue(iterator.Creation.CalleeInstance, out var callee))
                    continue;
                values.UnionWith(ObserveValues(callee, callee.Summary.Yields, ValueOrigin.None, out _));
            }
            return values;
        }

        // bounded: delegates once to the wrapped value, without re-entering this method.
        public IReadOnlySet<string> Resolve(MethodInstance instance, AbstractValue value) => Observation.Resolve(instance, value);

        /// <summary>Resolves every value and checks every alternative before their regions are merged.</summary>
        /// <param name="instance">The instance expressing the values.</param>
        /// <param name="values">The symbolic alternatives.</param>
        /// <param name="producers">Origins preserved through copies and merges.</param>
        /// <param name="unobserved">Whether any alternative cannot be observed.</param>
        public IReadOnlySet<string> ObserveValues(MethodInstance instance, IEnumerable<AbstractValue> values, ValueOrigin producers,
                                                   out bool unobserved)
        {
            var observed = ObserveUnrecorded(instance, values, producers, out var cause);
            unobserved = cause is not null;
            Unobserved ??= cause;
            return observed;
        }

        /// <summary>Resolves every value and checks every alternative, as <see cref="ObserveValues"/> does, and gives the cause an
        /// unobserved alternative records instead of recording it.</summary>
        /// <param name="instance">The instance expressing the values.</param>
        /// <param name="values">The symbolic alternatives.</param>
        /// <param name="producers">Origins preserved through copies and merges.</param>
        /// <param name="unobserved">The cause when any alternative cannot be observed, or <c>null</c>.</param>
        private IReadOnlySet<string> ObserveUnrecorded(MethodInstance instance, IEnumerable<AbstractValue> values, ValueOrigin producers,
                                                       out GenerationCause? unobserved)
        {
            var alternatives = values.Distinct().ToArray();
            unobserved = Observation.HasUnobservedValues(instance, alternatives, producers)
                ? new GenerationCause(GenerationReasons.VOCABULARY, ModelCauses.VALUE_UNOBSERVED, $"in {instance.BodyId}")
                : null;
            return alternatives.SelectMany(value => Resolve(instance, value)).ToHashSet(StringComparer.Ordinal);
        }

        private void BuildRoots()
        {
            foreach (var variant in _driver.Actions.Where(action => action != DriverSynthesizer.SETUP)
                                                   .Concat(_driver.Triggers.Select(trigger => trigger.Action)).Distinct(StringComparer.Ordinal))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (!Analysis.Executions.Any(execution => DriverExecutions.IsOwn(execution.Id, variant)))
                    continue;
                if (!_driver.Member.IsStatic && _driver.Member.MethodKind != MethodKind.Constructor)
                {
                    foreach (var region in StaticTargets(DriverSynthesizer.DRIVER_TYPE, $"Recv_{Variant(variant)}"))
                    {
                        _checks.Check(GenerationStage.ReadEffects);
                        AddRootGraph(region, new ThisValue());
                    }
                }
                foreach (var parameter in _driver.Member.Parameters.Where(parameter => parameter.RefKind != RefKind.Out &&
                                                                                TypeShape.Of(parameter.Type) != TypeShapeKind.Delegate))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    var field = parameter.RefKind == RefKind.Ref ? $"Out_{parameter.Name}_{Variant(variant)}" : $"Arg_{parameter.Name}_{Variant(variant)}";
                    var initial = parameter.RefKind == RefKind.Ref ? InitialValues(field) : StaticTargets(DriverSynthesizer.DRIVER_TYPE, field);
                    foreach (var region in initial)
                    {
                        _checks.Check(GenerationStage.ReadEffects);
                        AddRootGraph(region, new ArgumentValue(parameter.Name), parameter.Type);
                    }
                }
            }

            if (_driver.Member.MethodKind == MethodKind.Constructor)
            {
                foreach (var region in StaticTargets(DriverSynthesizer.KEEP_TYPE, "R"))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    AddRootGraph(region, new ThisValue());
                }
            }

            foreach (var parameter in _driver.Parameters)
            {
                _checks.Check(GenerationStage.ReadEffects);
                foreach (var probe in parameter.Probes)
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    var factory = Allocations.ProbeBody(probe);
                    foreach (var region in Heap.Regions.Values.Where(region => region.SiteBodyId == factory ||
                        region.SiteBodyId?.StartsWith(factory + "#", StringComparison.Ordinal) == true))
                    {
                        _checks.Check(GenerationStage.ReadEffects);
                        if (Allocations.Of(region.Identity).Role == DriverRole.ProbeLambdaReturn)
                            AddRootGraph(region.Identity, new ReturnsValue(parameter.Name));
                    }
                }
            }

            foreach (var trigger in _driver.Triggers)
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (!Analysis.Executions.Any(execution => DriverExecutions.IsOwn(execution.Id, trigger.Action)))
                    continue;
                var prefix = HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.DRIVER_TYPE,
                                                   $"TArg_{trigger.Action}_");
                foreach (var field in Reachability.StaticFields().Where(field => field.Slot.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    if (!int.TryParse(field.Slot[prefix.Length..], out var ordinal))
                        continue;
                    foreach (var region in Reachability.Targets(field))
                    {
                        _checks.Check(GenerationStage.ReadEffects);
                        AddRootGraph(region, new HolderArgumentValue(ordinal));
                    }
                }
            }
        }

        /// <summary>Names a root and what setup placed in it: its elements, and what a task argument completes with.</summary>
        /// <param name="root">The root region.</param>
        /// <param name="value">The root's name.</param>
        /// <param name="type">The root's type, or <c>null</c> when only its elements are named.</param>
        private void AddRootGraph(string root, LibraryValue value, ITypeSymbol? type = null)
        {
            var pending = new Stack<(string Region, LibraryValue Value, ITypeSymbol? Type)>();
            var seen = new HashSet<(string Region, string Value)>();
            pending.Push((root, value, type));
            while (pending.TryPop(out var item))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (!seen.Add((item.Region, item.Value.Canonical)))
                    continue;
                if (!_roots.TryGetValue(item.Region, out var roots))
                    _roots[item.Region] = roots = [];
                roots.Add(item.Value);
                // What setup placed names an element, whether or not the call stores the same value there again (R5). A pair's key and value
                // are no elements: it holds them as a struct holds its fields, which no value of the vocabulary names (R1).
                foreach (var edge in Reachability.Edges(item.Region).Where(edge => (edge.Field is ELEMENT or KEYS) && !IsPair(item.Region) &&
                    (!Written.Contains(new WrittenEdge(item.Region, edge.Field, edge.Target)) ||
                     _placed.Contains(new WrittenEdge(item.Region, edge.Field, edge.Target)))))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    pending.Push((edge.Target, new ElementsValue(item.Value), null));
                }
                // What a task argument completes with is named through it, at every level, as the value handed directly would be (R2).
                if (CompletionOf(item.Value, item.Type) is { } completion)
                {
                    foreach (var target in Heap.Completion(item.Region))
                    {
                        _checks.Check(GenerationStage.ReadEffects);
                        pending.Push((target, completion.Name, completion.Type));
                    }
                }
            }
        }

        /// <summary>Whether a region is a <c>KeyValuePair</c>, which holds its key and value apart in the storage a collection keeps its
        /// keys and cells in.</summary>
        /// <param name="region">The region.</param>
        private bool IsPair(string region) =>
            Heap.Regions.TryGetValue(region, out var held) && held.TypeKey is { } key &&
            key[(key.IndexOf(':') + 1)..].StartsWith("System.Collections.Generic.KeyValuePair<", StringComparison.Ordinal);

        private void BuildFired()
        {
            foreach (var access in Analysis.Accesses.Where(access => access.Access is
                { Kind: SummaryAccessKind.Store, Field.IsStatic: true, Field.Assembly: DriverSynthesizer.ASSEMBLY } &&
                access.Access.Field.ContainingTypeId == DriverSynthesizer.DRIVER_TYPE))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (!_fired.TryGetValue(access.Access.Field.Name, out var executions))
                    _fired[access.Access.Field.Name] = executions = new HashSet<string>(StringComparer.Ordinal);
                executions.UnionWith(access.Executions);
            }
        }

        private IReadOnlySet<WrittenEdge> ReadWrittenEdges()
        {
            var written = new HashSet<WrittenEdge>();
            foreach (var access in Run.Collection?.Accesses ?? [])
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (access.Operation is AccessOperation.Read or AccessOperation.AtomicRead || ActionOf(access.ExecutionId) is not
                    (DriverSynthesizer.CALL or DriverSynthesizer.ENUMERATE) || !IsLibraryBody(access.BodyId) ||
                    !Heap.Instances.TryGetValue(access.InstanceId, out var instance))
                {
                    continue;
                }
                var values = instance.Summary.Accesses.Where(summary => summary.OperationId == access.OperationId && summary.Kind == SummaryAccessKind.Store)
                                     .SelectMany(summary => summary.Values).SelectMany(value => Resolve(instance, value))
                                     .ToHashSet(StringComparer.Ordinal);
                foreach (var (@object, field) in Reachability.WrittenPlaces(access))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    AddExistingEdges(written, @object, field, values);
                }
            }

            var direct = MemberInstances(DriverSynthesizer.CALL).Select(instance => instance.Id)
                         .Concat(MemberInstances(DriverSynthesizer.ENUMERATE).Select(instance => instance.Id))
                         .ToHashSet(StringComparer.Ordinal);
            foreach (var instance in Heap.Instances.Values.Where(instance => IsLibraryBody(instance.BodyId) &&
                (direct.Contains(instance.Id) || Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) is { } executions &&
                 executions.Any(execution => ActionOf(execution) is DriverSynthesizer.CALL or DriverSynthesizer.ENUMERATE))))
            {
                _checks.Check(GenerationStage.ReadEffects);
                AddStoreEdges(written, instance);
            }
            return written;
        }

        /// <summary>Adds the edges an instance's own stores write: its field stores and stores through a reference, each at the place it
        /// writes (<see cref="Places"/>), its cell stores, and its copies of structs, each slot of the copy at the slot below the storage it
        /// goes into.</summary>
        /// <param name="edges">The edges found so far.</param>
        /// <param name="instance">The instance.</param>
        private void AddStoreEdges(HashSet<WrittenEdge> edges, MethodInstance instance)
        {
            foreach (var store in instance.Summary.Stores.Concat(instance.Summary.ReferenceStores))
            {
                _checks.Check(GenerationStage.ReadEffects);
                var values = store.Values.SelectMany(value => Resolve(instance, value)).ToHashSet(StringComparer.Ordinal);
                foreach (var (@object, field) in Places(instance, store.Bases, store.Field))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    AddExistingEdges(edges, @object, field, values);
                }
            }
            foreach (var store in instance.Summary.Elements.Concat(instance.Summary.ReferenceElementStores)
                                          .Where(store => store.Kind == ElementOperationKind.Store))
            {
                _checks.Check(GenerationStage.ReadEffects);
                var objects = store.Arrays.SelectMany(value => Resolve(instance, value));
                var values = store.Values.SelectMany(value => Resolve(instance, value)).ToHashSet(StringComparer.Ordinal);
                foreach (var @object in objects)
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    AddExistingEdges(edges, @object, store.Slot, values);
                }
            }
            foreach (var copy in instance.Summary.StructCopies)
            {
                _checks.Check(GenerationStage.ReadEffects);
                var held = copy.Sources.SelectMany(value => Resolve(instance, value)).SelectMany(Reachability.Edges)
                               .Select(edge => edge.Target).ToHashSet(StringComparer.Ordinal);
                foreach (var target in copy.Targets.SelectMany(value => Resolve(instance, value)))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    if (Heap.Located(target, "") is not var (@object, storage) || storage.Length == 0)
                        continue;
                    foreach (var edge in Reachability.Edges(@object).Where(edge => edge.Field.StartsWith(storage + FieldSlot.PATH_SEPARATOR, StringComparison.Ordinal) &&
                                                                                   held.Contains(edge.Target)))
                    {
                        _checks.Check(GenerationStage.ReadEffects);
                        edges.Add(new WrittenEdge(@object, edge.Field, edge.Target));
                    }
                }
            }
        }

        /// <summary>The places a store writes, each an object and the field of it written: a base that is a struct's storage stands as its
        /// holder, with the whole slot path through every struct field (R1); a base that is a struct copy is no place (R2); any other base
        /// is the object, with the field's name, which <see cref="FieldMatches"/> matches against the object's slots.</summary>
        /// <param name="instance">The instance storing.</param>
        /// <param name="bases">The store's bases.</param>
        /// <param name="field">The field stored.</param>
        private IEnumerable<(string Object, string Field)> Places(MethodInstance instance, IEnumerable<AbstractValue> bases, IrFieldRef field)
        {
            var slot = StructStorage.IsWholeStorage(field) ? "" : FieldSlot.Key(field);
            foreach (var region in bases.SelectMany(value => Resolve(instance, value)).Distinct(StringComparer.Ordinal))
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (Heap.Located(region, slot) is not var (@object, path))
                    continue;
                yield return @object == region ? (region, field.Name) : (@object, path);
            }
        }

        /// <summary>The edges setup placed: the stores of every instance, driver or library, that runs in setup and in nothing else.
        /// An instance that also runs in the call places nothing here, so a value only the call stored never reads as placed.</summary>
        private IReadOnlySet<WrittenEdge> ReadPlacedEdges()
        {
            var placed = new HashSet<WrittenEdge>();
            var setupOnly = Heap.Instances.Values.Where(instance => Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) is { Count: > 0 } executions &&
                                                                    executions.All(Executions.InSetup))
                                .ToDictionary(instance => instance.Id, StringComparer.Ordinal);
            foreach (var access in Run.Collection?.Accesses ?? [])
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (access.Operation is AccessOperation.Read or AccessOperation.AtomicRead ||
                    !setupOnly.TryGetValue(access.InstanceId, out var instance))
                {
                    continue;
                }
                var values = instance.Summary.Accesses.Where(summary => summary.OperationId == access.OperationId && summary.Kind == SummaryAccessKind.Store)
                                     .SelectMany(summary => summary.Values).SelectMany(value => Resolve(instance, value))
                                     .ToHashSet(StringComparer.Ordinal);
                foreach (var (@object, field) in Reachability.WrittenPlaces(access))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    AddExistingEdges(placed, @object, field, values);
                }
            }

            foreach (var instance in setupOnly.Values)
            {
                _checks.Check(GenerationStage.ReadEffects);
                AddStoreEdges(placed, instance);
            }
            return placed;
        }

        /// <summary>The delegate seeds setup put into a field: the stores made in a setup execution, whatever instance makes them —
        /// a field-like event's add accessor runs in setup and in the call alike. A value a store read back from the same field is
        /// what the field held, not what setup put there, so it does not count.</summary>
        private IReadOnlySet<WrittenEdge> ReadPlacedSeeds()
        {
            var placed = new HashSet<WrittenEdge>();
            foreach (var access in Run.Collection?.Accesses ?? [])
            {
                _checks.Check(GenerationStage.ReadEffects);
                if (access.Operation is AccessOperation.Read or AccessOperation.AtomicRead || !Executions.InSetup(access.ExecutionId) ||
                    !Heap.Instances.TryGetValue(access.InstanceId, out var instance))
                {
                    continue;
                }
                var values = instance.Summary.Accesses.Where(summary => summary.OperationId == access.OperationId && summary.Kind == SummaryAccessKind.Store)
                                     .SelectMany(summary => summary.Values.Where(value => !ReadsField(value, summary.Field)))
                                     .SelectMany(value => Observation.SetupValues(instance, value, Executions.InSetup))
                                     .Where(value => Heap.Regions.TryGetValue(value, out var region) && region.Kind == HeapRegionKind.Delegate &&
                                                     Allocations.Of(value).Role == DriverRole.Seed)
                                     .ToHashSet(StringComparer.Ordinal);
                foreach (var (@object, field) in Reachability.WrittenPlaces(access))
                {
                    _checks.Check(GenerationStage.ReadEffects);
                    AddExistingEdges(placed, @object, field, values);
                }
            }
            return placed;
        }

        /// <summary>Whether a value is a read of a field's own slot: the same static field, or a path ending in it.</summary>
        /// <param name="value">The value.</param>
        /// <param name="field">The field.</param>
        private static bool ReadsField(AbstractValue value, IrFieldRef field) => value switch
        {
            StaticFieldValue @static => FieldSlot.Key(@static.Field) == FieldSlot.Key(field),
            PathValue { Segments: [.., var last] } => last == FieldSlot.Key(field),
            _ => false
        };

        // A struct copy stored into a slot puts there what its reference fields hold, at the slot paths below it (R2).
        private void AddExistingEdges(HashSet<WrittenEdge> written, string @object, string field, IReadOnlySet<string> values)
        {
            var held = Held(values.Where(Reachability.IsCopy)).ToHashSet(StringComparer.Ordinal);
            foreach (var edge in Reachability.Edges(@object).Where(edge => FieldMatches(edge.Field, field) && values.Contains(edge.Target) ||
                                                                           held.Contains(edge.Target) && edge.Field.Contains(FieldSlot.PATH_SEPARATOR) &&
                                                                           FieldMatches(FieldSlot.Root(edge.Field), field)))
            {
                _checks.Check(GenerationStage.ReadEffects);
                written.Add(new WrittenEdge(@object, edge.Field, edge.Target));
            }
        }

        private bool IsLibraryBody(string body) => body.StartsWith($"body:{_driver.Member.ContainingAssembly.Name}:", StringComparison.Ordinal);

        // bounded: delegates once to the wrapped value, without re-entering this method.
        private static string DriverBody(string action) => ValueObservation.DriverBody(action);

        private static int ElementDepth(LibraryValue value)
        {
            WorkCancellation.Check();
            return value switch
            {
                ElementsValue elements => 1 + ElementDepth(elements.Source),
                // What a task completes with is no element of anything: it stands at the depth of a whole value.
                CompletionValue => 0,
                _ => 0
            };
        }

        // A bare field name is a slot of the object itself: a field of a struct its storage keeps is a slot path, matched whole (R1).
        private static bool FieldMatches(string edge, string field) => edge == field ||
            !edge.Contains(FieldSlot.PATH_SEPARATOR) && edge.EndsWith("." + field, StringComparison.Ordinal) ||
            field.StartsWith('[') && edge == ELEMENT;

        private static string Variant(string action) => action switch
        {
            DriverSynthesizer.CALL => DriverSynthesizer.CALL_VARIANT,
            DriverSynthesizer.ENUMERATE => DriverSynthesizer.ENUMERATE_VARIANT,
            _ => action
        };

        /// <summary>What a set of values of one instance stands for, as <see cref="Matches"/> decides it for each region.</summary>
        /// <param name="Regions">The regions the values resolve to.</param>
        /// <param name="Named">The values that may stand for a region by its name (<see cref="MatchesByName"/>): every value of the
        /// member's own entry, none of any other instance.</param>
        private sealed record ValueMatch(IReadOnlySet<string> Regions, IReadOnlyList<AbstractValue> Named);

        /// <summary>What one store puts into the objects it writes.</summary>
        /// <param name="Values">The stored objects, in the order the store's observation finds them.</param>
        /// <param name="Unobserved">The first cause the observation finds, or <c>null</c>.</param>
        private sealed record StoredValues(IReadOnlyList<string> Values, GenerationCause? Unobserved);

        /// <summary>A store DirectStores reads: a field store, or a store through a reference to a field.</summary>
        /// <param name="Places">Each object the store writes with the slot of it: for a field store the first place of each object
        /// (<see cref="Places"/>), for a store through a reference every field its targets name.</param>
        /// <param name="Bases">What a field store's bases stand for: an object one of them stands for that no place names is written at
        /// <paramref name="Field"/>. <c>null</c> for a store through a reference.</param>
        /// <param name="Field">The name of the field a field store names; empty for a store through a reference.</param>
        /// <param name="Stored">What it stores, observed on first use.</param>
        private sealed record FieldWrite(IReadOnlyList<(string Object, string Field)> Places, ValueMatch? Bases, string Field,
                                         Lazy<StoredValues> Stored);

        /// <summary>A store DirectElementStores reads: a cell store, or a store through a reference.</summary>
        /// <param name="Arrays">What a cell store's arrays stand for, or the arrays the targets of a store through a reference name.</param>
        /// <param name="Stored">What it stores, observed on first use.</param>
        private sealed record CellWrite(ValueMatch Arrays, Lazy<StoredValues> Stored);

        /// <summary>A store of the member's own call or enumeration entry, as <see cref="WasWritten"/> reads it: a field store, or a cell
        /// store with its slot standing as the field.</summary>
        /// <param name="Field">The field's name, or the cell store's slot.</param>
        /// <param name="Bases">What a field store's bases stand for, or the regions a cell store's arrays resolve to.</param>
        /// <param name="Places">The places a field store writes (<see cref="Places"/>); none for a cell store.</param>
        /// <param name="Values">What the stored values stand for, or the regions a cell store's values resolve to.</param>
        private sealed record MemberStore(string Field, ValueMatch Bases, IReadOnlyList<(string Object, string Field)> Places, ValueMatch Values);

        /// <summary>The stores of one set of instances, read once, each kept by the objects it may write.</summary>
        /// <param name="Fields">The stores DirectStores reads, in the order of its scan.</param>
        /// <param name="Cells">The stores DirectElementStores reads, in the order of its scan.</param>
        /// <param name="Unproven">The first store through a reference with no proven place, by its position among
        /// <paramref name="Cells"/>, with the cause the scan records for it whatever object it reads; <c>null</c> when every place is
        /// proven.</param>
        private sealed record StoreIndex(StoreTable<FieldWrite> Fields, StoreTable<CellWrite> Cells, (int Position, GenerationCause Cause)? Unproven);

        /// <summary>Stores kept by the objects they may write. A store's position is the order it was added in; the stores of an object
        /// are those added with it among their regions and those that may stand for an object by its name, in position order.</summary>
        /// <typeparam name="T">The kind of store.</typeparam>
        private sealed class StoreTable<T>
        {
            private readonly List<T> _stores = [];
            private readonly Dictionary<string, List<int>> _byObject = new(StringComparer.Ordinal);
            private readonly List<int> _named = [];

            /// <summary>The number of stores added: the position the next one takes.</summary>
            public int Count => _stores.Count;

            /// <summary>Adds a store at the next position.</summary>
            /// <param name="store">The store.</param>
            /// <param name="objects">The regions it may write.</param>
            /// <param name="named">Whether it may also write an object one of its values stands for by name.</param>
            public void Append(T store, IEnumerable<string> objects, bool named)
            {
                var position = _stores.Count;
                _stores.Add(store);
                foreach (var @object in objects)
                {
                    WorkCancellation.Check();
                    if (!_byObject.TryGetValue(@object, out var positions))
                        _byObject[@object] = positions = [];
                    if (positions.Count == 0 || positions[^1] != position)
                        positions.Add(position);
                }
                if (named)
                    _named.Add(position);
            }

            /// <summary>The stores that may write an object, with their positions, in position order.</summary>
            /// <param name="object">The object.</param>
            public IEnumerable<(int Position, T Store)> Of(string @object) =>
                (_byObject.GetValueOrDefault(@object) ?? []).Concat(_named).Distinct().Order().Select(position => (position, _stores[position]));
        }
    }
}
