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
    WholeDelegateInput
}

/// <summary>Names every object observed by a generated model entry and derives its result, keeps, outputs, stores and delegate
/// inputs. This is the one owner of value provenance: all names come from allocation origin and the place where the value was
/// observed.</summary>
public sealed class ValueProvenance
{
    private const int KEEPING_PATH_DEPTH = 8;
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
    private bool _vocabulary;

    /// <summary>Reads value provenance from the fate run and, for holder inputs only, its confirmation run.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="run">The fate run.</param>
    /// <param name="fates">The confirmed fates by member parameter.</param>
    /// <param name="confirmation">The holder confirmation run, or <c>null</c>.</param>
    public ValueProvenance(Driver driver, ScopeRun run, IReadOnlyDictionary<string, ClassifiedFate> fates, ScopeRun? confirmation)
    {
        if (run.Stopped)
            throw new ArgumentException("A stopped run has no values to name.", nameof(run));
        if (confirmation is { Stopped: true })
            confirmation = null;

        _driver = driver;
        _fates = fates;
        _fate = new RunContext(driver, run);
        _confirmation = confirmation is null ? null : new RunContext(driver, confirmation);

        if (_fate.CallInstances().Any(instance => run.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) &&
            body.Blocks.SelectMany(block => block.Operations).OfType<IrUnknownOperation>().Any(UnobservedStore)))
            _vocabulary = true;
        if (CarriedProbeFired())
            _vocabulary = true;

        var rawResult = ResultRegions();
        var rawOutputs = OutputRegions();
        var rawStores = StoreRegions();
        ReadInputs();
        ReadKeeps(rawResult);

        Observe(Shown(rawResult, _fate, ValuePlace.WholeResult), "result");
        foreach (var (parameter, regions) in rawOutputs)
            Observe(Shown(regions, _fate, ValuePlace.WholeOutput), $"output:{parameter}");
        foreach (var (parameter, regions) in rawStores)
            Observe(regions, $"store:{parameter}");
        foreach (var input in _rawInputs)
            Observe(Shown(input.Regions, input.Context, ValuePlace.WholeDelegateInput), $"input:{input.Parameter}:{input.Index}");
        foreach (var keeper in _keptByRegion.Values)
            Observe(keeper.Regions, $"keeps:{keeper.Name}");

        Keeps = BuildKeeps();
        Result = BuildMemberResult(rawResult);
        Outputs = BuildOutputs(rawOutputs);
        Stores = BuildStores(rawStores);
        Inputs = BuildInputs();
        HolderTriggers = BuildHolderTriggers();
        KeepingChain = _keepingChain.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Reason = _vocabulary || _fate.Unobserved || _confirmation is { Unobserved: true } ? GenerationReasons.VOCABULARY : null;
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

    /// <summary>For each holder fate, the declaration ids of the holder members whose trigger fired its probe.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> HolderTriggers { get; }

    /// <summary>For each keeper region that keeps something, the keeper and every pre-existing region on a keeping path.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> KeepingChain { get; }

    /// <summary><c>vocabulary</c> when an observed object has no name; otherwise <c>null</c>.</summary>
    public string? Reason { get; }

    /// <summary>The name of a region in the fate run at one entry place, or <c>null</c> when the vocabulary has none.</summary>
    /// <param name="region">The observed region.</param>
    /// <param name="place">Where it was observed.</param>
    public LibraryValue? Name(string region, ValuePlace place) => Name(region, place, _fate, ExpectedAction(place));

    /// <summary>The values a keeper region keeps, named by the model grammar.</summary>
    /// <param name="region">The keeper region.</param>
    public IReadOnlyList<LibraryValue> KeptBy(string region) =>
        _keptByRegion.TryGetValue(region, out var kept)
            ? Order(Names(kept.Regions, ValuePlace.KeptValue, _fate, null).Concat(_symbolicKeeps.GetValueOrDefault(region) ?? []))
            : [];

    /// <summary>Whether a region is one of the pre-existing objects on a path from a keeper to a value it keeps.</summary>
    /// <param name="region">The region written by the member.</param>
    public bool IsInKeepingChain(string region)
    {
        var chain = KeepingChain.Values.SelectMany(value => value).ToHashSet(StringComparer.Ordinal);
        if (chain.Contains(region) || !_fate.Heap.Regions.TryGetValue(region, out var stored))
            return chain.Contains(region);
        if (chain.Any(candidate => _fate.Heap.Regions.TryGetValue(candidate, out var kept) && kept.Group == stored.Group))
            return true;
        foreach (var (keeper, keepingChain) in KeepingChain)
        {
            var storedPaths = Paths(keeper, region);
            if (storedPaths.Count != 0 && keepingChain.SelectMany(candidate => Paths(keeper, candidate)).Any(storedPaths.Contains))
                return true;
        }
        return false;
    }

    /// <summary>The field paths from one region to another within the heap's bounded summary depth.</summary>
    /// <param name="start">The path's first region.</param>
    /// <param name="target">The path's last region.</param>
    private IReadOnlySet<string> Paths(string start, string target)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(string Region, string Path, int Depth)>();
        pending.Enqueue((start, "", 0));
        while (pending.TryDequeue(out var item))
        {
            if (item.Region == target)
                paths.Add(item.Path);
            if (item.Depth == KEEPING_PATH_DEPTH)
                continue;
            foreach (var edge in _fate.Reachability.Edges(item.Region))
            {
                var path = item.Path.Length == 0 ? edge.Field : item.Path + "/" + edge.Field;
                pending.Enqueue((edge.Target, path, item.Depth + 1));
            }
        }
        return paths;
    }

    private LibraryResult? BuildMemberResult(IReadOnlySet<string> regions)
    {
        if (_driver.Member.MethodKind == MethodKind.Constructor || ObservedResultType() is not { } type)
            return null;
        // What a task completes with has no name before run A4, whatever holds the delegate (question 94).
        if (TypeShape.Of(_driver.Member.ReturnType) == TypeShapeKind.TaskOfT && CanHoldObject(type))
        {
            _vocabulary = true;
            return null;
        }
        if (_fates.Values.Any(fate => fate.Fate == FateClassifier.HOLDER && fate.Holder == FateClassifier.RESULT))
            return null;
        if (regions.Count == 0 && SymbolicReturnNames() is { Count: > 0 } symbolic)
            return new LibraryResult(LibraryResultKind.OneOf, symbolic);
        if (regions.Count == 0 && type.ContainingAssembly?.Name == DriverSynthesizer.ASSEMBLY)
        {
            _vocabulary = true;
            return null;
        }
        if (regions.Count == 0 &&
            _fate.MemberInstances(DriverSynthesizer.CALL).Any(instance => instance.Summary.Returns.Any(returned => returned.Values.Count != 0)))
        {
            _vocabulary = true;
            return null;
        }
        return ResultOf(regions, type, ValuePlace.WholeResult, ValuePlace.ResultValue, _fate, DriverSynthesizer.CALL);
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
            foreach (var tree in reference.Compilation.SyntaxTrees.Where(tree => span.Path.Replace('\\', '/')
                .EndsWith(tree.FilePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            {
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

    private IReadOnlyList<LibraryValue> SymbolicReturnNames()
    {
        var values = new List<LibraryValue>();
        foreach (var returned in _fate.MemberInstances(DriverSynthesizer.CALL).SelectMany(instance => instance.Summary.Returns))
        {
            foreach (var value in returned.Values)
            {
                if (SymbolicName(value) is { } named)
                    values.Add(named);
            }
        }
        return Order(values);
    }

    /// <summary>The name an entry gives a value of the member's own frame, or <c>null</c> when it has none. A delegate parameter
    /// never has one: a fate speaks for it, and TD-034a refuses <c>arg:P</c> for a delegate-typed <c>P</c> (task 11).</summary>
    /// <param name="value">The value.</param>
    private LibraryValue? SymbolicName(AbstractValue value) => value switch
    {
        ParameterValue parameter when parameter.Ordinal >= 0 && parameter.Ordinal < _driver.Member.Parameters.Length &&
                                      TypeShape.Of(_driver.Member.Parameters[parameter.Ordinal].Type) != TypeShapeKind.Delegate =>
            new ArgumentValue(_driver.Member.Parameters[parameter.Ordinal].Name),
        ConcurrencyHunter.Heap.ThisValue => new ThisValue(),
        PathValue path when SymbolicName(path.Base) is { } root && path.Segments.All(segment => segment is ELEMENT or KEYS) =>
            path.Segments.Aggregate(root, (current, _) => (LibraryValue)new ElementsValue(current)),
        _ => null
    };

    private IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> BuildKeeps()
    {
        var keeps = new SortedDictionary<string, IReadOnlyList<LibraryValue>>(StringComparer.Ordinal);
        foreach (var keeper in _keptByRegion.Values.OrderBy(keeper => keeper.Name, StringComparer.Ordinal))
        {
            var symbolic = _symbolicKeeps.GetValueOrDefault(keeper.Region) ?? [];
            var values = Order(Names(keeper.Regions, ValuePlace.KeptValue, _fate, null).Concat(symbolic));
            if (values.Count != 0)
                keeps[keeper.Name] = values;
        }
        return keeps;
    }

    private IReadOnlyDictionary<string, LibraryResult> BuildOutputs(IReadOnlyDictionary<string, IReadOnlySet<string>> raw)
    {
        var outputs = new SortedDictionary<string, LibraryResult>(StringComparer.Ordinal);
        foreach (var (parameter, regions) in raw)
        {
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
                    _vocabulary = true;
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
            var count = group.Max(input => input.Index) + 1;
            var parameters = new IReadOnlyList<LibraryValue>[count];
            for (var index = 0; index < count; index++)
            {
                var at = group.Where(input => input.Index == index).ToArray();
                var values = new List<LibraryValue>();
                foreach (var input in at)
                    values.AddRange(Names(input.Regions, ValuePlace.WholeDelegateInput, input.Context, input.ExpectedActions));
                parameters[index] = Order(values);
            }
            inputs[group.Key] = parameters;
        }
        return inputs;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> BuildHolderTriggers()
    {
        var triggers = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (parameter, members) in _holderTriggers)
            triggers[parameter] = members.Order(StringComparer.Ordinal).ToArray();
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
            _vocabulary = true;
            return null;
        }

        var region = regions.Single();
        if (type is IArrayTypeSymbol || type is INamedTypeSymbol collection && SeedableFields.Collection(collection) is { Dictionary: false })
        {
            var values = Names(context.Heap.PointsTo(region, ELEMENT), valuePlace, context, null);
            if (values.Count != 0)
                return new LibraryResult(LibraryResultKind.Collection, values);
        }

        if (type is INamedTypeSymbol dictionary && SeedableFields.Collection(dictionary) is { Dictionary: true })
        {
            var keys = Names(context.Heap.PointsTo(region, KEYS), valuePlace, context, null);
            var values = Names(context.Heap.PointsTo(region, ELEMENT), valuePlace, context, null);
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

        _vocabulary = true;
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
            ReadKeeper(name, region);
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
            Push(pending, predecessors, keeper, edge.Target, _fate.WasWritten(new WrittenEdge(keeper, edge.Field, edge.Target)));
        foreach (var target in _fate.DirectStores(keeper))
            Push(pending, predecessors, keeper, target, true);
        foreach (var @object in _fate.Reachability.From([keeper]))
        {
            foreach (var target in _fate.DirectElementStores(@object))
            {
                if (@object != keeper)
                    AddPredecessor(predecessors, keeper, @object);
                Push(pending, predecessors, @object, target, true);
            }
        }

        while (pending.TryPop(out var state))
        {
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
                        _vocabulary = true;
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
                    _vocabulary = true;
                }
                continue;
            }

            if (_fate.Allocations.Of(state.Region).Kind == AllocationKind.Unknown)
            {
                if (state.Written)
                    _vocabulary = true;
                continue;
            }
            foreach (var target in _fate.DirectStores(state.Region))
                Push(pending, predecessors, state.Region, target, true);
            foreach (var target in _fate.DirectElementStores(state.Region))
                Push(pending, predecessors, state.Region, target, true);
            foreach (var edge in _fate.Reachability.Edges(state.Region))
                Push(pending, predecessors, state.Region, edge.Target, state.Written || _fate.WasWritten(new WrittenEdge(state.Region, edge.Field, edge.Target)));
        }
        AddChain(chain, Ancestors(predecessors, ends));

        var symbolic = _fate.CallInstances().Where(_fate.IsMemberEntry)
                            .SelectMany(instance => instance.Summary.Stores.Concat(instance.Summary.ReferenceStores)
                                .Where(store => store.Bases.Any(value => _fate.Matches(instance, value, keeper)))
                                .SelectMany(store => store.Values).Select(SymbolicName))
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
            chain.Add(region);
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
    private static IReadOnlySet<string> Ancestors(Dictionary<string, HashSet<string>> predecessors, IEnumerable<string> ends)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(ends);
        while (pending.TryPop(out var current))
        {
            if (!reached.Add(current))
                continue;
            foreach (var source in predecessors.GetValueOrDefault(current) ?? [])
                pending.Push(source);
        }
        return reached;
    }

    private IReadOnlySet<string> ResultRegions()
    {
        var result = _fate.StaticTargets(DriverSynthesizer.KEEP_TYPE, "R").ToHashSet(StringComparer.Ordinal);
        if (_driver.Member.MethodKind == MethodKind.Constructor)
            result.UnionWith(_fate.Heap.Regions.Keys.Where(region => _fate.Allocations.Of(region).Kind == AllocationKind.MemberObject));
        if (ObservedResultType() is { } type && CanHoldObject(type))
            foreach (var instance in _fate.MemberInstances(DriverSynthesizer.CALL))
                foreach (var returned in instance.Summary.Returns)
                    result.UnionWith(ObservedValues(_fate, instance, returned.Values, returned.Producers));
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
            _vocabulary = true;
        return observed;
    }

    private IReadOnlyDictionary<string, IReadOnlySet<string>> OutputRegions()
    {
        var outputs = new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var parameter in _driver.Member.Parameters.Where(parameter => parameter.RefKind is RefKind.Out or RefKind.Ref))
        {
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
            if (parameter is null)
            {
                _vocabulary = true;
                continue;
            }
            foreach (var value in parameter.Values)
            {
                values.UnionWith(ObservedValues(_fate, instance, [value], ValueOrigin.None));
                if (_fate.IsMemberEntry(instance) && SymbolicName(value) is { } named)
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
            var arrays = _fate.RootsOf(new ArgumentValue(parameter.Name)).ToHashSet(StringComparer.Ordinal);
            var values = _fate.Written.Where(edge => arrays.Contains(edge.Object) && edge.Field == ELEMENT)
                                      .Select(edge => edge.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var array in arrays)
                values.UnionWith(_fate.DirectElementStores(array));
            foreach (var instance in _fate.CallInstances())
            {
                foreach (var store in instance.Summary.Elements.Concat(instance.Summary.ReferenceElementStores)
                                              .Where(store => store.Kind == ElementOperationKind.Store &&
                                                              store.Arrays.Any(value => arrays.Any(array => _fate.Matches(instance, value, array)))))
                {
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
        var ran = FateClassifier.TriggersThatRan(_driver, parameter, _confirmation.Run);
        foreach (var probe in parameter.Probes.Where(probe => ran.Contains(probe.Variant)))
        {
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
            var regions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var probe in probes.Where(probe => probe.InputFields.Count > index))
            {
                var field = probe.InputFields[index];
                var type = _driver.Compilation.GetTypeByMetadataName(DriverSynthesizer.DRIVER_TYPE)!.GetMembers(field).OfType<IFieldSymbol>().Single().Type;
                if (!CanHoldObject(type))
                    continue;
                regions.UnionWith(context.InputRegions(field, expectedActions, out var unobserved));
                if (CanHoldObject(type) && unobserved)
                    _vocabulary = true;
            }
            _rawInputs.Add(new RawInput(parameter, index, regions, context, expectedActions));
        }
    }

    private bool CarriedProbeFired()
    {
        foreach (var parameter in _driver.Parameters.Where(parameter => parameter.Kind != ParameterKind.Delegate))
        {
            foreach (var probe in parameter.Probes.Where(probe => probe.Variant == DriverSynthesizer.CALL_VARIANT))
            {
                if (_fate.FiredExecutions(probe).Any(execution => _fate.Executions.InTree(execution, DriverSynthesizer.CALL)))
                    return true;
            }
        }
        return false;
    }

    private LibraryValue? Name(string region, ValuePlace place, RunContext context, IReadOnlySet<string>? expectedActions)
    {
        var names = context.RootNames(region);
        if (names.Count == 1)
            return names[0];
        if (names.Count > 1)
        {
            _vocabulary = true;
            return null;
        }

        if (place is not (ValuePlace.WholeResult or ValuePlace.WholeOutput or ValuePlace.WholeDelegateInput) ||
            !OnlyObservation(region, place, context) || !FreshGraph(region, place, context, expectedActions))
        {
            _vocabulary = true;
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
                _vocabulary = true;
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
                covered.UnionWith(context.Reachability.From([value]));
        }

        foreach (var reached in context.Reachability.From([region]))
        {
            if (covered.Contains(reached))
                continue;
            context.DirectStores(reached, expectedActions);
            context.DirectElementStores(reached, expectedActions);
            if (context.Unobserved || context.IsProbeDelegate(reached))
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
            shown.UnionWith(Graph(region, place, context));
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
        return context.Reachability.From([region]).Where(reached => !covered.Contains(reached));
    }

    private void Observe(IEnumerable<string> regions, string place)
    {
        foreach (var region in regions)
        {
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

    /// <summary>Follows each reference binding once on its path to an array cell, or to a field when <paramref name="fields"/>.</summary>
    /// <param name="heap">The solved heap.</param>
    /// <param name="instance">The instance expressing the reference.</param>
    /// <param name="target">The reference location.</param>
    /// <param name="fields">Whether a reference to a field names the object holding it.</param>
    /// <param name="seen">Bindings already visited.</param>
    /// <param name="unobserved">Whether any binding has no proven reference location.</param>
    private static IReadOnlySet<string> ReferenceArrays(HeapSolution heap, MethodInstance instance, ReferenceTarget target, bool fields,
                                                        HashSet<(string Instance, ReferenceTarget Target)> seen, ref bool unobserved)
    {
        if (!seen.Add((instance.Id, target)))
            return new HashSet<string>(StringComparer.Ordinal);
        switch (target)
        {
            case ReferenceParameterElement element:
                return instance.Parameters.GetValueOrDefault(element.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            case ReferenceCell { IsOnCollection: true } cell:
                return cell.Bases.SelectMany(value => heap.Resolve(instance.Id, value))
                           .SelectMany(region => heap.PointsTo(region, FieldSlot.Key(cell.Field))).ToHashSet(StringComparer.Ordinal);
            case ReferenceCell cell when fields:
                return cell.Bases.SelectMany(value => heap.Resolve(instance.Id, value)).ToHashSet(StringComparer.Ordinal);
            case ReferenceParameter parameter:
            {
                var arrays = new HashSet<string>(StringComparer.Ordinal);
                foreach (var edge in heap.Edges.Where(edge => edge.CalleeInstance == instance.Id))
                {
                    var caller = heap.Instances[edge.CallerInstance];
                    foreach (var argument in caller.Summary.Calls.Where(call => call.OperationId == edge.OperationId).SelectMany(call => call.Arguments)
                                                   .Where(argument => argument.ParameterOrdinal == parameter.Ordinal))
                    {
                        if (argument.References.Count == 0)
                        {
                            foreach (var path in argument.Values.OfType<PathValue>().Where(path => path.Segments.LastOrDefault() == ELEMENT))
                            {
                                var storage = path.Segments.Count == 1 ? path.Base : new PathValue(path.Base, path.Segments.Take(path.Segments.Count - 1).ToArray());
                                var regions = heap.Resolve(caller.Id, storage);
                                unobserved |= regions.Count == 0;
                                arrays.UnionWith(regions);
                            }
                        }
                        foreach (var reference in argument.References)
                            arrays.UnionWith(ReferenceArrays(heap, caller, reference, fields, seen, ref unobserved));
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
                    if (callee.Summary.ReferenceReturns.Count == 0)
                        unobserved = true;
                    foreach (var reference in callee.Summary.ReferenceReturns)
                        arrays.UnionWith(ReferenceArrays(heap, callee, reference, fields, seen, ref unobserved));
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

        public RunContext(Driver driver, ScopeRun run)
        {
            _driver = driver;
            Run = run;
            Heap = run.Heap!;
            Analysis = run.Executions!;
            Observation = new ValueObservation(driver, run, GenerationHandoffs.UnfollowedCalls(run).Select(call => (call.Instance.Id, call.OperationId))
                                                                                                    .ToHashSet());
            Reachability = new HeapReachability(Heap);
            var result = Reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.KEEP_TYPE, "R"));
            Executions = new DriverExecutions(driver, Analysis, Reachability.From(result is null ? [] : Reachability.Targets(result)));
            Allocations = new Allocations(driver, Heap, Analysis, Executions, Reachability);
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
        public bool Unobserved { get; private set; }

        public IReadOnlySet<string> StaticTargets(string type, string field)
        {
            var start = Reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, type, field));
            return start is null ? new HashSet<string>(StringComparer.Ordinal) : Reachability.Targets(start);
        }

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

        public string? ActionOf(string execution)
        {
            var role = Executions.Of(execution);
            return role is { Role: DriverExecutionRole.Own or DriverExecutionRole.Child, Action: not null } ? role.Action : null;
        }

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
                    var caller = Heap.Instances[edge.CallerInstance];
                    foreach (var argument in caller.Summary.Calls.Where(call => call.OperationId == edge.OperationId)
                                                   .SelectMany(call => call.Arguments).Where(argument => ordinals.Contains(argument.ParameterOrdinal)))
                    {
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
                foreach (var parameter in instance.Summary.RefParameters.Where(parameter =>
                    RefersToParameter(instance, new ReferenceParameter(parameter.Ordinal), ordinal, roots, [])))
                {
                    yield return (instance, parameter);
                }
                if (!Run.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
                    continue;
                foreach (var access in instance.Summary.ReferenceAccesses.Where(access => access.Kind == SummaryAccessKind.Store &&
                    access.Targets.Any(target => RefersToParameter(instance, target, ordinal, roots, []))))
                {
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
            foreach (var instance in MemberInstances(DriverSynthesizer.CALL).Concat(MemberInstances(DriverSynthesizer.ENUMERATE)))
            {
                if (instance.Summary.Stores.Concat(instance.Summary.ReferenceStores).Any(store =>
                    FieldMatches(edge.Field, store.Field.Name) && store.Bases.Any(value => Matches(instance, value, edge.Object)) &&
                    store.Values.Any(value => Matches(instance, value, edge.Value))))
                {
                    return true;
                }
                if (instance.Summary.Elements.Concat(instance.Summary.ReferenceElementStores).Any(store => store.Kind == ElementOperationKind.Store &&
                    FieldMatches(edge.Field, store.Slot) && store.Arrays.SelectMany(value => Resolve(instance, value)).Contains(edge.Object) &&
                    store.Values.SelectMany(value => Resolve(instance, value)).Contains(edge.Value)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The complete observations of values stored into an object's fields.</summary>
        /// <param name="object">The object region.</param>
        /// <param name="actions">The observed actions, or null for the member's call and enumeration.</param>
        public IReadOnlySet<string> DirectStores(string @object, IReadOnlySet<string>? actions = null)
        {
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var instance in StoreInstances(actions))
            {
                foreach (var store in instance.Summary.Stores.Concat(instance.Summary.ReferenceStores)
                                              .Where(store => FieldCarriesObject(store.Field) && store.Bases.Any(value => Matches(instance, value, @object))))
                {
                    values.UnionWith(ObserveValues(instance, store.Values, store.Producers, out _)
                                         .Where(value => !IsPlacedSlotSeed(@object, store.Field.Name, value)));
                }
            }
            return values;
        }

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

        /// <summary>The regions stored directly into cells of an array by the member.</summary>
        /// <param name="object">The array region.</param>
        /// <param name="actions">The observed actions, or null for the member's call and enumeration.</param>
        public IReadOnlySet<string> DirectElementStores(string @object, IReadOnlySet<string>? actions = null)
        {
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var instance in StoreInstances(actions))
            {
                foreach (var store in instance.Summary.Elements.Concat(instance.Summary.ReferenceElementStores)
                                              .Where(store => store.Kind == ElementOperationKind.Store &&
                                                              store.Arrays.Any(value => Matches(instance, value, @object))))
                {
                    values.UnionWith(ObserveValues(instance, store.Values, store.Producers, out _));
                }
                foreach (var access in instance.Summary.ReferenceAccesses.Where(access => access.Kind == SummaryAccessKind.Store))
                {
                    var arrays = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var target in access.Targets)
                    {
                        arrays.UnionWith(ReferenceArrays(Heap, instance, target, out var unobserved));
                        Unobserved |= unobserved;
                    }
                    if (!arrays.Contains(@object))
                        continue;
                    if (!Run.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
                        continue;
                    var store = body.Blocks.SelectMany(block => block.Operations).OfType<IrStoreReferenceOperation>()
                                    .FirstOrDefault(store => store.Id == access.OperationId);
                    if (store is null)
                        continue;
                    var symbol = body.Values[store.Value].SymbolKey;
                    var variable = symbol is null ? null : instance.Summary.Variables.FirstOrDefault(variable => variable.SymbolKey == symbol);
                    if (variable is not null)
                        values.UnionWith(ObserveValues(instance, variable.Values, ValueOrigin.None, out _));
                    else
                    {
                        var transfers = instance.Summary.RefParameters.Where(parameter => access.Targets.Contains(new ReferenceParameter(parameter.Ordinal))).ToArray();
                        foreach (var parameter in transfers)
                            values.UnionWith(ObserveValues(instance, parameter.Values, ValueOrigin.None, out _));
                        if (transfers.Length == 0)
                            Unobserved = true;
                    }
                }
            }
            return values;
        }

        private IEnumerable<MethodInstance> StoreInstances(IReadOnlySet<string>? actions) => actions is null ? CallInstances() :
            Heap.Instances.Values.Where(instance => IsLibraryBody(instance.BodyId) &&
                (Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>(StringComparer.Ordinal))
                             .Any(execution => ActionOf(execution) is { } action && actions.Contains(action)));

        public bool IsMemberEntry(MethodInstance instance) => Observation.IsMemberEntry(instance);

        public IEnumerable<MethodInstance> CallInstances() => Heap.Instances.Values.Where(instance => IsLibraryBody(instance.BodyId) &&
            (Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>(StringComparer.Ordinal))
                         .Any(execution => ActionOf(execution) is DriverSynthesizer.CALL or DriverSynthesizer.ENUMERATE));

        public bool Matches(MethodInstance instance, AbstractValue value, string region)
        {
            if (Resolve(instance, value).Contains(region))
                return true;
            if (!IsMemberEntry(instance))
                return false;
            return value switch
            {
                ConcurrencyHunter.Heap.ThisValue => RootNames(region).Any(root => root is ThisValue) ||
                                                    Allocations.Of(region).Kind == AllocationKind.MemberObject,
                ParameterValue parameter when parameter.Ordinal >= 0 && parameter.Ordinal < _driver.Member.Parameters.Length =>
                    RootNames(region).Any(root => root is ArgumentValue argument &&
                                                  argument.Parameter == _driver.Member.Parameters[parameter.Ordinal].Name),
                _ => false
            };
        }

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
                if (!Heap.Instances.TryGetValue(iterator.Creation.CalleeInstance, out var callee))
                    continue;
                values.UnionWith(ObserveValues(callee, callee.Summary.Yields, ValueOrigin.None, out _));
            }
            return values;
        }

        public IReadOnlySet<string> Resolve(MethodInstance instance, AbstractValue value) => Observation.Resolve(instance, value);

        /// <summary>Resolves every value and checks every alternative before their regions are merged.</summary>
        /// <param name="instance">The instance expressing the values.</param>
        /// <param name="values">The symbolic alternatives.</param>
        /// <param name="producers">Origins preserved through copies and merges.</param>
        /// <param name="unobserved">Whether any alternative cannot be observed.</param>
        public IReadOnlySet<string> ObserveValues(MethodInstance instance, IEnumerable<AbstractValue> values, ValueOrigin producers,
                                                   out bool unobserved)
        {
            var alternatives = values.Distinct().ToArray();
            unobserved = Observation.HasUnobservedValues(instance, alternatives, producers);
            Unobserved |= unobserved;
            return alternatives.SelectMany(value => Resolve(instance, value)).ToHashSet(StringComparer.Ordinal);
        }

        private void BuildRoots()
        {
            foreach (var variant in _driver.Actions.Where(action => action != DriverSynthesizer.SETUP)
                                                   .Concat(_driver.Triggers.Select(trigger => trigger.Action)).Distinct(StringComparer.Ordinal))
            {
                if (!Analysis.Executions.Any(execution => DriverExecutions.IsOwn(execution.Id, variant)))
                    continue;
                if (!_driver.Member.IsStatic && _driver.Member.MethodKind != MethodKind.Constructor)
                {
                    foreach (var region in StaticTargets(DriverSynthesizer.DRIVER_TYPE, $"Recv_{Variant(variant)}"))
                        AddRootGraph(region, new ThisValue());
                }
                foreach (var parameter in _driver.Member.Parameters.Where(parameter => parameter.RefKind != RefKind.Out &&
                                                                                TypeShape.Of(parameter.Type) != TypeShapeKind.Delegate))
                {
                    var field = parameter.RefKind == RefKind.Ref ? $"Out_{parameter.Name}_{Variant(variant)}" : $"Arg_{parameter.Name}_{Variant(variant)}";
                    var initial = parameter.RefKind == RefKind.Ref ? InitialValues(field) : StaticTargets(DriverSynthesizer.DRIVER_TYPE, field);
                    foreach (var region in initial)
                    {
                        AddRootGraph(region, new ArgumentValue(parameter.Name));
                    }
                }
            }

            if (_driver.Member.MethodKind == MethodKind.Constructor)
            {
                foreach (var region in StaticTargets(DriverSynthesizer.KEEP_TYPE, "R"))
                    AddRootGraph(region, new ThisValue());
            }

            foreach (var parameter in _driver.Parameters)
            {
                foreach (var probe in parameter.Probes)
                {
                    var factory = Allocations.ProbeBody(probe);
                    foreach (var region in Heap.Regions.Values.Where(region => region.SiteBodyId == factory ||
                        region.SiteBodyId?.StartsWith(factory + "#", StringComparison.Ordinal) == true))
                    {
                        if (Allocations.Of(region.Identity).Role == DriverRole.ProbeLambdaReturn)
                            AddRootGraph(region.Identity, new ReturnsValue(parameter.Name));
                    }
                }
            }

            foreach (var trigger in _driver.Triggers)
            {
                if (!Analysis.Executions.Any(execution => DriverExecutions.IsOwn(execution.Id, trigger.Action)))
                    continue;
                var prefix = HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.DRIVER_TYPE,
                                                   $"TArg_{trigger.Action}_");
                foreach (var field in Reachability.StaticFields().Where(field => field.Slot.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    if (!int.TryParse(field.Slot[prefix.Length..], out var ordinal))
                        continue;
                    foreach (var region in Reachability.Targets(field))
                        AddRootGraph(region, new HolderArgumentValue(ordinal));
                }
            }
        }

        private void AddRootGraph(string root, LibraryValue value)
        {
            var pending = new Stack<(string Region, LibraryValue Value)>();
            var seen = new HashSet<(string Region, string Value)>();
            pending.Push((root, value));
            while (pending.TryPop(out var item))
            {
                if (!seen.Add((item.Region, item.Value.Canonical)))
                    continue;
                if (!_roots.TryGetValue(item.Region, out var roots))
                    _roots[item.Region] = roots = [];
                roots.Add(item.Value);
                // What setup placed names an element, whether or not the call stores the same value there again (R5).
                foreach (var edge in Reachability.Edges(item.Region).Where(edge => (edge.Field is ELEMENT or KEYS) &&
                    (!Written.Contains(new WrittenEdge(item.Region, edge.Field, edge.Target)) ||
                     _placed.Contains(new WrittenEdge(item.Region, edge.Field, edge.Target)))))
                    pending.Push((edge.Target, new ElementsValue(item.Value)));
            }
        }

        private void BuildFired()
        {
            foreach (var access in Analysis.Accesses.Where(access => access.Access is
                { Kind: SummaryAccessKind.Store, Field.IsStatic: true, Field.Assembly: DriverSynthesizer.ASSEMBLY } &&
                access.Access.Field.ContainingTypeId == DriverSynthesizer.DRIVER_TYPE))
            {
                if (!_fired.TryGetValue(access.Access.Field.Name, out var executions))
                    _fired[access.Access.Field.Name] = executions = new HashSet<string>(StringComparer.Ordinal);
                executions.Add(access.ExecutionId);
            }
        }

        private IReadOnlySet<WrittenEdge> ReadWrittenEdges()
        {
            var written = new HashSet<WrittenEdge>();
            foreach (var access in Run.Collection?.Accesses ?? [])
            {
                if (access.Operation is AccessOperation.Read or AccessOperation.AtomicRead || ActionOf(access.ExecutionId) is not
                    (DriverSynthesizer.CALL or DriverSynthesizer.ENUMERATE) || !IsLibraryBody(access.BodyId) ||
                    !Heap.Instances.TryGetValue(access.InstanceId, out var instance))
                {
                    continue;
                }
                var values = instance.Summary.Accesses.Where(summary => summary.OperationId == access.OperationId && summary.Kind == SummaryAccessKind.Store)
                                     .SelectMany(summary => summary.Values).SelectMany(value => Resolve(instance, value))
                                     .ToHashSet(StringComparer.Ordinal);
                var field = EdgeField(access.Resource.AccessPath.LastOrDefault() ?? access.Resource.Member.Name);
                foreach (var @object in Reachability.WrittenObjects(access))
                    AddExistingEdges(written, @object, field, values);
            }

            var direct = MemberInstances(DriverSynthesizer.CALL).Select(instance => instance.Id)
                         .Concat(MemberInstances(DriverSynthesizer.ENUMERATE).Select(instance => instance.Id))
                         .ToHashSet(StringComparer.Ordinal);
            foreach (var instance in Heap.Instances.Values.Where(instance => IsLibraryBody(instance.BodyId) &&
                (direct.Contains(instance.Id) || Analysis.InstanceExecutions.GetValueOrDefault(instance.Id) is { } executions &&
                 executions.Any(execution => ActionOf(execution) is DriverSynthesizer.CALL or DriverSynthesizer.ENUMERATE))))
            {
                foreach (var store in instance.Summary.Stores.Concat(instance.Summary.ReferenceStores))
                {
                    var objects = store.Bases.SelectMany(value => Resolve(instance, value));
                    var values = store.Values.SelectMany(value => Resolve(instance, value)).ToHashSet(StringComparer.Ordinal);
                    foreach (var @object in objects)
                        AddExistingEdges(written, @object, store.Field.Name, values);
                }
                foreach (var store in instance.Summary.Elements.Concat(instance.Summary.ReferenceElementStores)
                                              .Where(store => store.Kind == ElementOperationKind.Store))
                {
                    var objects = store.Arrays.SelectMany(value => Resolve(instance, value));
                    var values = store.Values.SelectMany(value => Resolve(instance, value)).ToHashSet(StringComparer.Ordinal);
                    foreach (var @object in objects)
                        AddExistingEdges(written, @object, store.Slot, values);
                }
            }
            return written;
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
                if (access.Operation is AccessOperation.Read or AccessOperation.AtomicRead ||
                    !setupOnly.TryGetValue(access.InstanceId, out var instance))
                {
                    continue;
                }
                var values = instance.Summary.Accesses.Where(summary => summary.OperationId == access.OperationId && summary.Kind == SummaryAccessKind.Store)
                                     .SelectMany(summary => summary.Values).SelectMany(value => Resolve(instance, value))
                                     .ToHashSet(StringComparer.Ordinal);
                var field = EdgeField(access.Resource.AccessPath.LastOrDefault() ?? access.Resource.Member.Name);
                foreach (var @object in Reachability.WrittenObjects(access))
                    AddExistingEdges(placed, @object, field, values);
            }

            foreach (var instance in setupOnly.Values)
            {
                foreach (var store in instance.Summary.Stores.Concat(instance.Summary.ReferenceStores))
                {
                    var values = store.Values.SelectMany(value => Resolve(instance, value)).ToHashSet(StringComparer.Ordinal);
                    foreach (var @object in store.Bases.SelectMany(value => Resolve(instance, value)))
                        AddExistingEdges(placed, @object, store.Field.Name, values);
                }
                foreach (var store in instance.Summary.Elements.Concat(instance.Summary.ReferenceElementStores)
                                              .Where(store => store.Kind == ElementOperationKind.Store))
                {
                    var values = store.Values.SelectMany(value => Resolve(instance, value)).ToHashSet(StringComparer.Ordinal);
                    foreach (var @object in store.Arrays.SelectMany(value => Resolve(instance, value)))
                        AddExistingEdges(placed, @object, store.Slot, values);
                }
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
                if (access.Operation is AccessOperation.Read or AccessOperation.AtomicRead || !Executions.InSetup(access.ExecutionId) ||
                    !Heap.Instances.TryGetValue(access.InstanceId, out var instance))
                {
                    continue;
                }
                var values = instance.Summary.Accesses.Where(summary => summary.OperationId == access.OperationId && summary.Kind == SummaryAccessKind.Store)
                                     .SelectMany(summary => summary.Values.Where(value => !ReadsField(value, summary.Field)))
                                     .SelectMany(value => Resolve(instance, value))
                                     .Where(value => Heap.Regions.TryGetValue(value, out var region) && region.Kind == HeapRegionKind.Delegate &&
                                                     Allocations.Of(value).Role == DriverRole.Seed)
                                     .ToHashSet(StringComparer.Ordinal);
                var field = EdgeField(access.Resource.AccessPath.LastOrDefault() ?? access.Resource.Member.Name);
                foreach (var @object in Reachability.WrittenObjects(access))
                    AddExistingEdges(placed, @object, field, values);
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

        private void AddExistingEdges(HashSet<WrittenEdge> written, string @object, string field, IReadOnlySet<string> values)
        {
            foreach (var edge in Reachability.Edges(@object).Where(edge => FieldMatches(edge.Field, field) && values.Contains(edge.Target)))
                written.Add(new WrittenEdge(@object, edge.Field, edge.Target));
        }

        private bool IsLibraryBody(string body) => body.StartsWith($"body:{_driver.Member.ContainingAssembly.Name}:", StringComparison.Ordinal);

        private static string DriverBody(string action) => ValueObservation.DriverBody(action);

        private static int ElementDepth(LibraryValue value) => value is ElementsValue elements ? 1 + ElementDepth(elements.Source) : 0;

        private static bool FieldMatches(string edge, string field) => edge == field || edge.EndsWith("." + field, StringComparison.Ordinal) ||
            field.StartsWith('[') && edge == ELEMENT;

        private static string EdgeField(string field) => field.StartsWith('[') ? ELEMENT : field;

        private static string Variant(string action) => action switch
        {
            DriverSynthesizer.CALL => DriverSynthesizer.CALL_VARIANT,
            DriverSynthesizer.ENUMERATE => DriverSynthesizer.ENUMERATE_VARIANT,
            _ => action
        };
    }
}
