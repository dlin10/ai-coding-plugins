using System.Text.RegularExpressions;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Heap;

/// <summary>Builds a body's summary the first time it is asked for, and counts how many it built.</summary>
public sealed class SummaryCache(IReadOnlyDictionary<string, IrBody> bodies, ProgramIndex program, AnalysisLimits limits)
{
    private readonly Dictionary<string, MethodSummary> _summaries = new(StringComparer.Ordinal);

    public int Built => _summaries.Count;

    /// <summary>The limits the summaries are built under, which the accesses expanded from them keep too.</summary>
    public AnalysisLimits Limits => limits;

    public bool IsBuilt(string bodyId) => _summaries.ContainsKey(bodyId);

    public MethodSummary? Get(string bodyId)
    {
        if (_summaries.TryGetValue(bodyId, out var summary))
            return summary;
        if (!bodies.TryGetValue(bodyId, out var body))
            return null;
        summary = MethodSummaryBuilder.Build(body, program, limits, id => bodies.GetValueOrDefault(id));
        _summaries.Add(bodyId, summary);
        return summary;
    }
}

/// <summary>Everything the whole-program fixpoint of one process scope is solved over.</summary>
public sealed record ScopeProgram(string ScopeId, IReadOnlyList<ExecutionRootDescriptor> Roots, ReachableSetResult Reachable,
                                  SummaryCache Summaries, ProgramIndex Program, DiIndex DiIndex,
                                  IReadOnlyList<TypeInjectionBindings> InjectionBindings);

public enum HeapRegionKind
{
    Static,
    Di,
    Allocation,
    Receiver,
    Delegate,
    Container,
    Provider,
    Symbolic,
    Task
}

/// <summary>One object or set of objects. <see cref="Identity"/> includes the context; <see cref="Display"/> never does.
/// An open region names a type parameter and stands for every closed region of its <see cref="Group"/>.
/// <see cref="MayOverlapItself"/> marks a hosted service whose instance count is unknown. <see cref="SiteBodyId"/> and
/// <see cref="SiteOperationId"/> name an allocation's or a delegate creation's site.</summary>
public sealed record HeapRegion(string Identity, HeapRegionKind Kind, string Display, string? TypeKey, string Context, string Group,
                                bool IsOpen, bool IsMerged, bool MayOverlapItself = false, string? SiteBodyId = null, int? SiteOperationId = null);

/// <summary>An instantiation of a body: its context, type substitution and the regions its receiver and parameters point to.
/// A receiverless instance has no <c>this</c>; its accesses based on <c>this</c> have no resource.</summary>
public sealed record MethodInstance(string Id, string BodyId, string Context, IReadOnlyDictionary<string, string> Substitution,
                                    bool IsMerged, bool IsReceiverless, MethodSummary Summary, IReadOnlySet<string> Receivers,
                                    IReadOnlyDictionary<int, IReadOnlySet<string>> Parameters, IReadOnlySet<string> CellOwners);

public sealed record CallEdge(string CallerInstance, int OperationId, string CalleeInstance, string Reason);

public sealed record IteratorObject(string RegionId, CallEdge Creation);

/// <summary>A closed type's type initializer: the instance running it and what triggered it.</summary>
public sealed record TypeInitializerConstruction(string TypeKey, string InstanceId, IReadOnlyList<string> TriggeringInstances,
                                                 IReadOnlyList<string> TriggeringRegions);

/// <summary>A region the container or the framework constructs, with the constructor instances that run for it.</summary>
public sealed record RegionConstruction(string RegionId, IReadOnlyList<string> ConstructorInstances);

/// <summary>An operation of an instance that triggers the construction of a region: a locator call, or the entry (operation
/// <c>-1</c>) of a root or constructor body whose injections resolve it.</summary>
public sealed record RegionTrigger(string InstanceId, int OperationId, string RegionId);

public enum SpawnRole
{
    Work,
    LocalInit,
    LocalFinally
}

public sealed record SpawnCallee(string InstanceId, SpawnRole Role);

/// <summary>A spawn of a caller instance (<see cref="SummarySpawn"/>): the handle regions its site creates in the caller's context,
/// or the started threads; the tail region when the handle's work returns a task the handle does not wait for; the instances it
/// runs. These are not call edges: the spawned bodies run as their own work.</summary>
public sealed record SpawnSite(string CallerInstance, int OperationId, int CallOperationId, IrSpawnKind Kind, IReadOnlySet<string> Handles,
                               string? Tail, IReadOnlyList<SpawnCallee> Callees);

/// <summary>A timer's callback or <c>Elapsed</c> handler (<see cref="SummaryTimer"/>) with the timer regions and the instances it runs.</summary>
public sealed record TimerCallbackSite(string CallerInstance, int OperationId, IrTimerAction Action, IReadOnlySet<string> Timers,
                                       IReadOnlyList<string> Callees)
{
    /// <summary>Whether <see cref="Timers"/> names every timer the site may run: an unknown origin may be any timer, whatever the timers
    /// beside it are.</summary>
    public bool TimersKnown { get; init; } = true;
}

/// <summary>A resolved call edge into an async body whose result is not awaited at once: an <see cref="IrSpawnKind.AsyncCall"/>,
/// whose result is the <see cref="Handle"/> region of the site in the caller's context, or an <see cref="IrSpawnKind.AsyncVoid"/>.
/// The edges themselves stay call edges: the body runs in the caller up to its first await.</summary>
public sealed record AsyncSpawnSite(string CallerInstance, int OperationId, IrSpawnKind Kind, string? Handle, IReadOnlyList<string> Callees);

/// <summary>A delegate region handed to unresolved calls (R3): the calls that handed it, by caller instance and operation, and the
/// instances running its target. These are not call edges: the body runs in an unknown execution of its own.</summary>
public sealed record DelegateHandoff(string RegionId, IReadOnlyList<(string CallerInstance, int OperationId)> Sites, IReadOnlyList<string> Callees);

/// <summary>The tasks a <c>Task.WhenAll</c> result completes after, or that they are unknown.</summary>
public sealed record TaskGroup(IReadOnlySet<string> Members, bool MembersKnown);

public static class HeapCounters
{
    public const string MERGED_CONTEXT = "merged-context";
    public const string SCC_BUDGET_EXCEEDED = "scc-budget-exceeded";
    public const string NO_RECEIVER_OBJECT = "no-receiver-object";
    public const string REACHABLE_BODIES = "reachable-bodies";
    public const string LOWERED_NOT_REACHED = "lowered-not-reached";
    public const string UNRESOLVED_LOCATOR = "unresolved-locator";
}

/// <summary>The solved heap of one scope.</summary>
public sealed class HeapSolution
{
    private readonly Func<string, AbstractValue, IReadOnlySet<string>> _resolve;
    private readonly Func<string, string, IReadOnlySet<string>> _load;
    private readonly Func<string, string, IReadOnlySet<string>> _cell;
    private readonly Func<string, IrFieldRef, string> _staticRegion;
    private readonly Func<string, IReadOnlyList<string>> _fieldsOf;
    private readonly Func<string, IReadOnlySet<string>> _delegateCaptures;

    internal HeapSolution(IReadOnlyDictionary<string, HeapRegion> regions, IReadOnlyDictionary<string, MethodInstance> instances,
                          IReadOnlyList<CallEdge> edges, IReadOnlyList<TypeInitializerConstruction> typeInitializers,
                          IReadOnlyList<RegionConstruction> constructions, IReadOnlyDictionary<string, int> counters,
                          IReadOnlySet<string> reachableBodies, IReadOnlyList<string> loweredNotReached,
                          IReadOnlyList<(string BodyId, int OperationId)> noReceiverObjects,
                          Func<string, AbstractValue, IReadOnlySet<string>> resolve, Func<string, string, IReadOnlySet<string>> load,
                          Func<string, string, IReadOnlySet<string>> cell, IReadOnlyDictionary<string, string> rootInstances,
                          Func<string, IrFieldRef, string> staticRegion, Func<string, IReadOnlyList<string>> fieldsOf,
                          Func<string, IReadOnlySet<string>> delegateCaptures)
    {
        _cell = cell;
        RootInstances = rootInstances;
        _staticRegion = staticRegion;
        _fieldsOf = fieldsOf;
        _delegateCaptures = delegateCaptures;
        Regions = regions;
        Instances = instances;
        Edges = edges;
        TypeInitializers = typeInitializers;
        Constructions = constructions;
        Counters = counters;
        ReachableBodies = reachableBodies;
        LoweredNotReached = loweredNotReached;
        NoReceiverObjects = noReceiverObjects;
        _resolve = resolve;
        _load = load;
    }

    public IReadOnlyDictionary<string, HeapRegion> Regions { get; }
    public IReadOnlyDictionary<string, MethodInstance> Instances { get; }
    public IReadOnlyList<CallEdge> Edges { get; }
    public IReadOnlyList<CallEdge> ExecutionEdges { get; init; } = [];
    public IReadOnlyList<IteratorObject> IteratorObjects { get; init; } = [];
    public IReadOnlySet<string> UnknownIterators { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlyList<TypeInitializerConstruction> TypeInitializers { get; }
    public IReadOnlyList<RegionConstruction> Constructions { get; }
    public IReadOnlyDictionary<string, int> Counters { get; }
    public IReadOnlySet<string> ReachableBodies { get; }
    public IReadOnlyList<string> LoweredNotReached { get; }
    public IReadOnlyList<(string BodyId, int OperationId)> NoReceiverObjects { get; }

    /// <summary>The call results whose value may come from an origin points-to does not follow: the callee may return an object the heap
    /// cannot name, so the regions of the result are not all the result may be.</summary>
    public IReadOnlySet<(string Instance, int Operation)> UnfollowedCallResults { get; init; } = new HashSet<(string, int)>();

    /// <summary>The virtual, interface and delegate calls whose receiver may come from such an origin: the edges of the call name the
    /// bodies the heap could resolve, not every body the call may run.</summary>
    public IReadOnlySet<(string Instance, int Operation)> UnresolvedCallTargets { get; init; } = new HashSet<(string, int)>();

    /// <summary>The locator calls that stayed opaque, once per body and operation.</summary>
    public IReadOnlyList<(string BodyId, int OperationId)> UnresolvedLocators { get; init; } = [];

    /// <summary>The virtual, interface and delegate calls of each instance with no receiver object at all: unresolved dispatch, which
    /// calls nothing the heap has (R1).</summary>
    public IReadOnlySet<(string Instance, int Operation)> UnresolvedDispatches { get; init; } = new HashSet<(string, int)>();

    /// <summary>For each unresolved dispatch, the receiver objects whose implementation has no body, each with the type of the run's own
    /// declaring that implementation, whose state alone the call sees through it (R1); null where no type of the run's own declares it.</summary>
    public IReadOnlyDictionary<(string Instance, int Operation), IReadOnlySet<(string Region, string? DeclaringTypeKey)>> UnresolvedDispatchReceivers { get; init; } =
        new Dictionary<(string, int), IReadOnlySet<(string, string?)>>();

    /// <summary>The delegates handed to unresolved calls, each run in an unknown execution of its own (R3), by region.</summary>
    public IReadOnlyList<DelegateHandoff> DelegateHandoffs { get; init; } = [];

    /// <summary>What the analysis could not know about a region: a symbolic parameter's unknown caller, a registration whose
    /// factory or instance gives more than one object or an unknown one.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RegionUncertainties { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The objects of each <c>GetServices</c> result region, in the registrations' total order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Collections { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The regions whose construction runs at startup: the container, whose constructions are the registration-holding
    /// members and the entry point, and the objects of instance registrations.</summary>
    public IReadOnlySet<string> StartupRegions { get; init; } = new HashSet<string>();

    /// <summary>The instances whose locator calls resolved each object that has no execution of its own: a transient, or a scoped
    /// object of a created scope. Such an object is created where those instances run.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> LocatorCreators { get; init; } = new Dictionary<string, IReadOnlySet<string>>();

    /// <summary>The operations that trigger the constructions of regions, in instance id, operation and region order.</summary>
    public IReadOnlyList<RegionTrigger> RegionTriggers { get; init; } = [];

    /// <summary>The entry instance of each root, by stable root id.</summary>
    public IReadOnlyDictionary<string, string> RootInstances { get; }

    /// <summary>The spawns of every instance, in caller and operation order.</summary>
    public IReadOnlyList<SpawnSite> Spawns { get; init; } = [];

    public IReadOnlyList<TimerCallbackSite> TimerCallbacks { get; init; } = [];

    public IReadOnlyList<AsyncSpawnSite> AsyncSpawns { get; init; } = [];

    /// <summary>The tail region of each handle whose work returns a task: what <c>Unwrap()</c> and an await of the awaited handle give.</summary>
    public IReadOnlyDictionary<string, string> Tails { get; init; } = new Dictionary<string, string>();

    /// <summary>The antecedent regions of each continuation handle.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> Antecedents { get; init; } = new Dictionary<string, IReadOnlySet<string>>();

    /// <summary>The group of each <c>Task.WhenAll</c> result region.</summary>
    public IReadOnlyDictionary<string, TaskGroup> TaskGroups { get; init; } = new Dictionary<string, TaskGroup>();

    /// <summary>The storage region of a static field as an instance's substitution names it.</summary>
    public string StaticRegionOf(string instanceId, IrFieldRef field) => _staticRegion(instanceId, field);

    /// <summary>The fields (and <c>[]</c>) through which a region points to other regions.</summary>
    public IReadOnlyList<string> FieldsOf(string regionId) => _fieldsOf(regionId);

    /// <summary>The regions a delegate region captures: its receiver and the cells of the variables it closes over.</summary>
    public IReadOnlySet<string> DelegateCaptures(string regionId) => _delegateCaptures(regionId);

    /// <summary>The regions an abstract value of an instance's summary points to.</summary>
    public IReadOnlySet<string> Resolve(string instanceId, AbstractValue value) =>
        value is RegionValue region ? new HashSet<string>(StringComparer.Ordinal) { region.RegionId } : _resolve(instanceId, value);

    /// <summary>The regions a field of a region points to, including what open regions of its group store. The field is a slot key
    /// as <see cref="FieldsOf"/> gives it, <c>[]</c>, or a bare field name, which joins the slots of every declaring type with that
    /// name.</summary>
    public IReadOnlySet<string> PointsTo(string regionId, string field) => _load(regionId, field);

    /// <summary>The regions the capture cell of a symbol key in a member-body instance points to.</summary>
    public IReadOnlySet<string> Cell(string ownerInstanceId, string symbolKey) => _cell(ownerInstanceId, symbolKey);
}

/// <summary>
/// Andersen-style propagation over <c>(method, context)</c> instances created on demand from the roots and the constructions
/// they trigger. An instance method's context is its receiver region, a static method's its call site; allocations take the
/// instance's context. Contexts beyond <see cref="AnalysisLimits.MaxContextsPerMethod"/>, a substitution naming an open type
/// parameter, and a call-graph cycle that keeps changing for more than <see cref="AnalysisLimits.MaxSccIterations"/> rounds go
/// to one merged context per body. Points-to sets only grow over a finite universe, so the rounds reach a fixpoint.
/// </summary>
public static class WholeProgram
{
    public static HeapSolution Solve(ScopeProgram program, AnalysisLimits limits) => new Solver(program, limits).Run();

    private static readonly Regex ASSEMBLY_PREFIX = new(@"(?<=^|[<\s,])[^<>,\s:\[\]*]+:");

    /// <summary>A type key as <c>SymbolNames.Type</c> displays it: every assembly prefix removed.</summary>
    internal static string DisplayType(string typeKey) => ASSEMBLY_PREFIX.Replace(typeKey, "");

    /// <summary>The reason of a call edge from a locator call to the constructor or factory instances of the object it resolved.</summary>
    public const string CONSTRUCTION_REASON = "construction";

    private const string SERVICE_PROVIDER_TYPE = ":System.IServiceProvider";
    private const string STARTUP_CONTEXT = "startup";
    private const string SERVICE_COLLECTION_TYPE = ":Microsoft.Extensions.DependencyInjection.IServiceCollection";

    /// <summary>Where a DI resolution happens: inside one HTTP invocation, in the root scope, or in the scope object a
    /// <c>CreateScope</c> call created.</summary>
    private sealed record ResolutionScope(string? InvocationRootId, string? CreatedScope = null)
    {
        internal string Context => CreatedScope is { } created ? $"scope:{created}"
            : InvocationRootId is { } rootId ? $"invocation:root:{rootId}"
            : "root-scope";
    }

    /// <summary>A factory or instance registration's region: the scope its factory runs for, the factory instances, and whether
    /// its object fell back to the region itself because nothing flowed to it.</summary>
    private sealed class RegistrationState(string region, DiRegistration registration, ResolutionScope scope)
    {
        internal string Region { get; } = region;
        internal DiRegistration Registration { get; } = registration;
        internal ResolutionScope Scope { get; } = scope;
        internal HashSet<string> Instances { get; } = new(StringComparer.Ordinal);
        internal bool Defaulted { get; set; }
    }

    private sealed class InstanceState(string id, string bodyId, string context, IReadOnlyDictionary<string, string> substitution,
                                       bool isMerged, bool isReceiverless, MethodSummary summary)
    {
        internal string Id { get; } = id;
        internal string BodyId { get; } = bodyId;
        internal string Context { get; } = context;
        internal IReadOnlyDictionary<string, string> Substitution { get; } = substitution;
        internal bool IsMerged { get; } = isMerged;
        internal bool IsReceiverless { get; } = isReceiverless;
        internal MethodSummary Summary { get; } = summary;
        internal HashSet<string> Receivers { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> CellOwners { get; } = new(StringComparer.Ordinal);
        internal Dictionary<int, HashSet<string>> Parameters { get; } = [];
        internal Dictionary<int, HashSet<string>> CallResults { get; } = [];

        /// <summary>The calls whose result, and the returns of this instance, may come from an origin points-to does not follow.</summary>
        internal HashSet<int> UnfollowedCallResults { get; } = [];

        /// <summary>The calls whose receiver may come from such an origin, so their edges are not all the targets they may run.</summary>
        internal HashSet<int> UnresolvedCallTargets { get; } = [];
        internal bool ReturnsUnfollowed { get; set; }
        internal Dictionary<(int Operation, int Ordinal), HashSet<string>> RefResults { get; } = [];
        internal HashSet<string> Returns { get; } = new(StringComparer.Ordinal);
        internal Dictionary<int, HashSet<string>> RefParameters { get; } = [];

        /// <summary>The HTTP invocations, by root id, whose request this instance runs in.</summary>
        internal HashSet<string> Requests { get; } = new(StringComparer.Ordinal);
    }

    private sealed class DelegateState(string target, bool isNestedBody, string? containingTypeKey, IReadOnlyList<string> methodTypeArguments,
                                       IReadOnlyDictionary<string, string> ownerSubstitution, bool isNonVirtual)
    {
        internal string Target { get; } = target;
        internal bool IsNestedBody { get; } = isNestedBody;

        /// <summary>The method group was named through <c>base</c>: its target runs on each receiver without dispatch.</summary>
        internal bool IsNonVirtual { get; } = isNonVirtual;
        internal string? ContainingTypeKey { get; } = containingTypeKey;
        internal IReadOnlyList<string> MethodTypeArguments { get; } = methodTypeArguments;
        internal IReadOnlyDictionary<string, string> OwnerSubstitution { get; } = ownerSubstitution;
        internal HashSet<string> CellOwners { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> CapturedReceivers { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> CapturedKeys { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>What a spawn, async spawn or timer callback site has run so far: a spawn has a <see cref="Kind"/> and a call, a
    /// timer callback an <see cref="Action"/>, and only a spawn a <see cref="Tail"/>.</summary>
    private sealed class SiteState(IrSpawnKind? kind, int callOperationId, IrTimerAction? action = null)
    {
        internal IrSpawnKind? Kind { get; } = kind;
        internal int CallOperationId { get; } = callOperationId;
        internal IrTimerAction? Action { get; } = action;
        internal HashSet<string> Handles { get; } = new(StringComparer.Ordinal);
        internal string? Tail { get; set; }
        internal HashSet<(string Instance, SpawnRole Role)> Callees { get; } = [];
    }

    private sealed class Solver
    {
        private static readonly IReadOnlyDictionary<string, string> NO_SUBSTITUTION = new Dictionary<string, string>();

        /// <summary>The synthetic field of a <c>Thread</c> region holding the work its constructor bound.</summary>
        private const string THREAD_WORK_FIELD = "<thread-work>";

        private readonly ScopeProgram _scope;
        private readonly ProgramIndex _program;
        private readonly AnalysisLimits _limits;
        private readonly Dictionary<string, string> _memberOfNestedBody = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _capturedKeysOfMember = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HeapRegion> _regions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _groups = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Region, string Field), HashSet<string>> _fields = [];
        private readonly Dictionary<(string Owner, string Key), HashSet<string>> _cells = [];
        private readonly Dictionary<string, DelegateState> _delegates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, InstanceState> _instances = new(StringComparer.Ordinal);
        private readonly List<string> _instanceOrder = [];
        private readonly Dictionary<string, int> _contextCounts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _mergedBodies = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sccHandled = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _changedRounds = new(StringComparer.Ordinal);
        private readonly HashSet<(string Caller, int Operation, string Callee, string Reason)> _edges = [];
        private readonly Dictionary<string, CallEdge> _iteratorObjects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string InstanceId, HashSet<string> Instances, HashSet<string> Regions)> _typeInitializers =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _constructions = new(StringComparer.Ordinal);
        private readonly HashSet<(string BodyId, int OperationId)> _noReceiver = [];
        private readonly HashSet<(string Instance, int Operation)> _unresolvedDispatches = [];
        private readonly Dictionary<(string Instance, int Operation), HashSet<(string Region, string? DeclaringTypeKey)>> _unresolvedReceivers = [];
        private readonly Dictionary<string, (HashSet<(string Caller, int Operation)> Sites, HashSet<string> Callees)> _handoffs = new(StringComparer.Ordinal);
        private UnknownCalls.Modelled? _modelled;
        private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _rootInstances = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ResolutionScope> _providers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _scopeObjects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RegistrationState> _registrations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _factoryInstances = new(StringComparer.Ordinal);
        private readonly Dictionary<(string BodyId, int OperationId, string Context), string> _registeredAllocations = [];
        private readonly Dictionary<string, HashSet<string>> _regionTypes = new(StringComparer.Ordinal);
        private readonly HashSet<(HashSet<string> Target, string Region)> _sinks = [];
        private readonly Dictionary<string, List<string>> _uncertainties = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _collections = new(StringComparer.Ordinal);
        private readonly HashSet<string> _startupRegions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _locatorCreators = new(StringComparer.Ordinal);
        private readonly HashSet<(string InstanceId, int OperationId, string RegionId)> _regionTriggers = [];
        private readonly Dictionary<(string BodyId, int OperationId), (string BodyId, SummaryOpaqueCall Call)?> _sites = [];
        private readonly Dictionary<string, DiRegistration[]> _instanceRegistrationsByMember = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Caller, int Operation), SiteState> _spawns = [];
        private readonly Dictionary<(string Caller, int Operation), SiteState> _timerCallbacks = [];
        private readonly Dictionary<(string Caller, int Operation), SiteState> _asyncSpawns = [];
        private readonly Dictionary<string, string> _tails = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _antecedents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (HashSet<string> Members, bool Known)> _taskGroups = new(StringComparer.Ordinal);
        private int _changes;

        internal Solver(ScopeProgram scope, AnalysisLimits limits)
        {
            _scope = scope;
            _program = scope.Program;
            _limits = limits;
            foreach (var method in _program.Methods)
            {
                foreach (var nested in method.NestedBodyIds)
                    _memberOfNestedBody.TryAdd(nested, method.MethodId);
            }

            foreach (var group in RegistrationsWithBodies(DiRegistrationForm.Instance).GroupBy(registration => registration.BodyId!, StringComparer.Ordinal))
                _instanceRegistrationsByMember.Add(group.Key, group.ToArray());
        }

        internal HeapSolution Run()
        {
            Seed();
            do
            {
                Propagate();
            }
            while (DefaultEmptyRegistrations());

            return Result();
        }

        private void Propagate()
        {
            while (true)
            {
                var before = _changes;
                // A registration's created types arrive during the fixpoint; the last, unchanged pass records the calls with no receiver.
                _noReceiver.Clear();
                _unresolvedDispatches.Clear();
                _unresolvedReceivers.Clear();
                _handoffs.Clear();
                for (var index = 0; index < _instanceOrder.Count; index++)
                {
                    var instance = _instances[_instanceOrder[index]];
                    var start = _changes;
                    Process(instance);
                    if (_changes != start)
                        CountRound(instance);
                }

                foreach (var state in _registrations.Values.ToArray())
                    ConstructFactory(state);
                foreach (var (target, region) in _sinks.ToArray())
                    Add(target, Object(region));

                if (_changes == before)
                    break;
            }
        }

        /// <summary>A factory or instance registration nothing flowed to keeps its own region, with no created type.</summary>
        private bool DefaultEmptyRegistrations()
        {
            var defaulted = false;
            foreach (var state in _registrations.Values.Where(state => !state.Defaulted).ToArray())
            {
                if (Object(state.Region).Count != 0)
                    continue;
                state.Defaulted = true;
                defaulted = true;
                _changes++;
            }

            return defaulted;
        }

        private IEnumerable<DiRegistration> RegistrationsWithBodies(DiRegistrationForm form) =>
            _scope.DiIndex.Registrations.Where(registration => registration is { IsSupported: true, BodyId: not null, OperationId: not null,
                                                                                  ServiceTypeKey: not null, ImplementationTypeKey: not null } &&
                                                               registration.Form == form);

        private HeapSolution Result()
        {
            var reachable = _instances.Values.Select(instance => instance.BodyId).ToHashSet(StringComparer.Ordinal);
            // Every lowered body, nested ones included: a lambda the heap never reaches is inventory too.
            var loweredNotReached = _scope.Reachable.Bodies.Keys.Where(body => !reachable.Contains(body))
                                          .Order(StringComparer.Ordinal).ToArray();
            _counters[HeapCounters.NO_RECEIVER_OBJECT] = _noReceiver.Count;
            _counters[HeapCounters.REACHABLE_BODIES] = reachable.Count;
            _counters[HeapCounters.LOWERED_NOT_REACHED] = loweredNotReached.Length;
            _counters.TryAdd(HeapCounters.MERGED_CONTEXT, 0);
            _counters.TryAdd(HeapCounters.SCC_BUDGET_EXCEEDED, 0);
            var unresolved = UnresolvedLocators();
            _counters[HeapCounters.UNRESOLVED_LOCATOR] = unresolved.Count;
            RegistrationUncertainties();

            var instances = _instances.Values.ToDictionary(
                instance => instance.Id,
                instance => new MethodInstance(instance.Id, instance.BodyId, instance.Context, instance.Substitution, instance.IsMerged,
                                               instance.IsReceiverless, instance.Summary, instance.Receivers,
                                               instance.Parameters.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value),
                                               instance.CellOwners),
                StringComparer.Ordinal);
            var executionEdges = _edges.Where(edge => _scope.Reachable.Bodies.GetValueOrDefault(_instances[edge.Callee].BodyId)?.IsIterator != true)
                                       .Select(edge => new CallEdge(edge.Caller, edge.Operation, edge.Callee, edge.Reason)).ToList();
            var unknownIterators = new HashSet<string>(StringComparer.Ordinal);
            foreach (var instance in _instances.Values.Where(_ => _iteratorObjects.Count != 0))
            {
                if (!_scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
                    continue;
                var calls = body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>().ToDictionary(call => call.Id);
                foreach (var call in instance.Summary.OpaqueCalls)
                {
                    if (!calls.TryGetValue(call.OperationId, out var operation))
                        continue;
                    var candidateValues = call.Receivers.Concat(call.Arguments.SelectMany(argument => argument.Values))
                                              .Where(PotentialIteratorValue).ToArray();
                    if (candidateValues.Length == 0)
                        continue;
                    var iteratorRegions = Eval(instance, candidateValues)
                        .Where(_iteratorObjects.ContainsKey).ToArray();
                    foreach (var region in iteratorRegions)
                    {
                        if (operation.EnumerationRole == IrEnumerationRole.GetEnumerator &&
                            Eval(instance, call.Receivers).Contains(region))
                            executionEdges.Add(new CallEdge(instance.Id, call.OperationId, _iteratorObjects[region].CalleeInstance,
                                                            "iterator-enumeration"));
                        else if (operation.EnumerationRole == IrEnumerationRole.None)
                            unknownIterators.Add(region);
                    }
                }
            }
            // An iterator escapes when a field keeps it, or keeps a delegate that captured it: whoever calls that delegate enumerates it
            // (open question 24). The delegate itself, stored without a visible call, runs nowhere of its own.
            foreach (var values in _fields.Values)
            {
                unknownIterators.UnionWith(values.Where(_iteratorObjects.ContainsKey));
                unknownIterators.UnionWith(values.Where(_delegates.ContainsKey).SelectMany(Captures).Where(_iteratorObjects.ContainsKey));
            }

            static bool PotentialIteratorValue(AbstractValue value) => value switch
            {
                AllocationValue or DelegateCreationValue or AwaitResultValue => false,
                PathValue path => PotentialIteratorValue(path.Base),
                _ => true
            };

            return new HeapSolution(
                _regions,
                instances,
                _edges.OrderBy(edge => edge.Caller, StringComparer.Ordinal).ThenBy(edge => edge.Operation)
                      .ThenBy(edge => edge.Callee, StringComparer.Ordinal)
                      .Select(edge => new CallEdge(edge.Caller, edge.Operation, edge.Callee, edge.Reason)).ToArray(),
                _typeInitializers.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                 .Select(pair => new TypeInitializerConstruction(pair.Key, pair.Value.InstanceId,
                                                                                 pair.Value.Instances.Order(StringComparer.Ordinal).ToArray(),
                                                                                 pair.Value.Regions.Order(StringComparer.Ordinal).ToArray()))
                                 .ToArray(),
                _constructions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                              .Select(pair => new RegionConstruction(pair.Key, pair.Value.ToArray())).ToArray(),
                _counters,
                reachable,
                loweredNotReached,
                _noReceiver.OrderBy(item => item.BodyId, StringComparer.Ordinal).ThenBy(item => item.OperationId).ToArray(),
                (instanceId, value) => Eval(_instances[instanceId], value),
                LoadAny,
                (owner, key) => _cells.GetValueOrDefault((owner, key)) ?? new HashSet<string>(StringComparer.Ordinal),
                _rootInstances,
                (instanceId, field) => StaticRegion(_instances[instanceId], field),
                regionId => _fields.Where(pair => pair.Key.Region == regionId && pair.Value.Count != 0).Select(pair => pair.Key.Field)
                                   .Order(StringComparer.Ordinal).ToArray(),
                regionId => _delegates.ContainsKey(regionId) ? Captures(regionId) : new HashSet<string>(StringComparer.Ordinal))
            {
                ExecutionEdges = executionEdges.OrderBy(edge => edge.CallerInstance, StringComparer.Ordinal).ThenBy(edge => edge.OperationId)
                                              .ThenBy(edge => edge.CalleeInstance, StringComparer.Ordinal).ToArray(),
                IteratorObjects = _iteratorObjects.Select(pair => new IteratorObject(pair.Key, pair.Value)).ToArray(),
                UnknownIterators = unknownIterators,
                UnresolvedLocators = unresolved.OrderBy(item => item.BodyId, StringComparer.Ordinal).ThenBy(item => item.OperationId).ToArray(),
                UnresolvedDispatches = _unresolvedDispatches.ToHashSet(),
                UnresolvedDispatchReceivers = _unresolvedReceivers.ToDictionary(pair => pair.Key,
                                                                                pair => (IReadOnlySet<(string, string?)>)pair.Value.ToHashSet()),
                DelegateHandoffs = _handoffs.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                            .Select(pair => new DelegateHandoff(pair.Key,
                                                                               pair.Value.Sites.OrderBy(site => site.Caller, StringComparer.Ordinal)
                                                                                   .ThenBy(site => site.Operation).ToArray(),
                                                                               pair.Value.Callees.Order(StringComparer.Ordinal).ToArray()))
                                            .ToArray(),
                RegionUncertainties = _uncertainties.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(), StringComparer.Ordinal),
                Collections = _collections.ToDictionary(pair => pair.Key,
                                                        pair => (IReadOnlyList<string>)pair.Value.SelectMany(Object).Distinct(StringComparer.Ordinal).ToArray(),
                                                        StringComparer.Ordinal),
                StartupRegions = _startupRegions,
                UnfollowedCallResults = _instances.Values
                                                  .SelectMany(instance => instance.UnfollowedCallResults.Select(operation => (instance.Id, operation)))
                                                  .ToHashSet(),
                UnresolvedCallTargets = _instances.Values
                                                  .SelectMany(instance => instance.UnresolvedCallTargets.Select(operation => (instance.Id, operation)))
                                                  .ToHashSet(),
                LocatorCreators = _locatorCreators.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.Ordinal),
                RegionTriggers = _regionTriggers.OrderBy(item => item.InstanceId, StringComparer.Ordinal).ThenBy(item => item.OperationId)
                                                .ThenBy(item => item.RegionId, StringComparer.Ordinal)
                                                .Select(item => new RegionTrigger(item.InstanceId, item.OperationId, item.RegionId)).ToArray(),
                Spawns = Ordered(_spawns).Select(pair => new SpawnSite(pair.Key.Caller, pair.Key.Operation, pair.Value.CallOperationId, pair.Value.Kind!.Value,
                                                                       Sorted(pair.Value.Handles), pair.Value.Tail,
                                                                       pair.Value.Callees.OrderBy(callee => callee.Instance, StringComparer.Ordinal)
                                                                           .ThenBy(callee => callee.Role)
                                                                           .Select(callee => new SpawnCallee(callee.Instance, callee.Role)).ToArray()))
                                         .ToArray(),
                TimerCallbacks = Ordered(_timerCallbacks).Select(pair => new TimerCallbackSite(pair.Key.Caller, pair.Key.Operation, pair.Value.Action!.Value,
                                                                                               Sorted(pair.Value.Handles), Callees(pair.Value))
                {
                    TimersKnown = TimersKnown(pair.Key.Caller, pair.Key.Operation, pair.Value.Handles)
                })
                                                         .ToArray(),
                AsyncSpawns = Ordered(_asyncSpawns).Select(pair => new AsyncSpawnSite(pair.Key.Caller, pair.Key.Operation, pair.Value.Kind!.Value,
                                                                                      pair.Value.Handles.SingleOrDefault(), Callees(pair.Value)))
                                                   .ToArray(),
                Tails = _tails,
                Antecedents = _antecedents.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.Ordinal),
                TaskGroups = _taskGroups.ToDictionary(pair => pair.Key, pair => new TaskGroup(pair.Value.Members, pair.Value.Known), StringComparer.Ordinal)
            };

            static IEnumerable<KeyValuePair<(string Caller, int Operation), SiteState>> Ordered(Dictionary<(string Caller, int Operation), SiteState> sites) =>
                sites.OrderBy(pair => pair.Key.Caller, StringComparer.Ordinal).ThenBy(pair => pair.Key.Operation);

            static IReadOnlySet<string> Sorted(IEnumerable<string> regions) => new SortedSet<string>(regions, StringComparer.Ordinal);

            static IReadOnlyList<string> Callees(SiteState site) =>
                site.Callees.Select(callee => callee.Instance).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }

        /// <summary>What a delegate region captures: the receiver it was created on and the values of the variables it closes over.</summary>
        private HashSet<string> Captures(string regionId)
        {
            var state = _delegates[regionId];
            return state.CapturedReceivers.Concat(state.CellOwners.SelectMany(owner => state.CapturedKeys.SelectMany(key => Cell(owner, key))))
                        .ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>The locator calls of every instance that resolve nothing: no constant type, a receiver standing for no scope,
        /// or a service that binds nothing.</summary>
        private HashSet<(string BodyId, int OperationId)> UnresolvedLocators()
        {
            var unresolved = new HashSet<(string BodyId, int OperationId)>();
            foreach (var instance in _instances.Values)
            {
                foreach (var call in instance.Summary.OpaqueCalls.Where(call => call.ServiceCall is { Kind: not IrServiceCallKind.ScopeCreation }))
                {
                    var service = call.ServiceCall!;
                    var resolves = service.ServiceTypeKey is { } key && Scopes(instance, call).Count != 0 &&
                                   (service.Kind == IrServiceCallKind.Locator
                                       ? key.EndsWith(SERVICE_PROVIDER_TYPE, StringComparison.Ordinal) || _scope.DiIndex.Resolve(key).Kind == DiResolutionKind.Bound
                                       : Supported(_scope.DiIndex.Resolve(key)).Any());
                    if (!resolves)
                        unresolved.Add((instance.BodyId, call.OperationId));
                }
            }

            return unresolved;
        }

        /// <summary>A registration whose object fell back to its own region has an unknown result; one whose object is more than
        /// one region gives each of them the uncertainty that it is more than one object.</summary>
        private void RegistrationUncertainties()
        {
            foreach (var state in _registrations.Values.OrderBy(state => state.Region, StringComparer.Ordinal))
            {
                var region = _regions[state.Region];
                var source = $"registration at {state.Registration.Source.Path}:{state.Registration.Source.StartLine}";
                if (state.Defaulted && Object(state.Region).Count == 1)
                {
                    Uncertain(state.Region, $"{region.Display}: factory result is unknown ({source}).");
                    continue;
                }

                var objects = Object(state.Region);
                if (objects.Count > 1)
                {
                    foreach (var target in objects.Order(StringComparer.Ordinal))
                        Uncertain(target, $"{region.Display}: factory returns more than one object ({source}).");
                }
            }
        }

        private void Uncertain(string region, string uncertainty)
        {
            if (!_uncertainties.TryGetValue(region, out var list))
                _uncertainties.Add(region, list = []);
            if (!list.Contains(uncertainty))
                list.Add(uncertainty);
        }

        /// <summary>Runs the entry point and every member holding a factory or instance registration as the container's startup
        /// construction. A member another startup member reaches through its call chain is instantiated by those calls; any other (and
        /// every member of a call cycle no other member reaches) has its service collection bound to the container and every other
        /// parameter to a symbolic region of an unknown caller.</summary>
        private void SeedStartup()
        {
            var members = ReachableSet.StartupMembers(_program, _scope.DiIndex).Where(_scope.Reachable.Bodies.ContainsKey).ToArray();
            if (members.Length == 0)
                return;

            var container = Region($"container|{_scope.ScopeId}", HeapRegionKind.Container, "container", null, STARTUP_CONTEXT, null);
            _startupRegions.Add(container);
            var callees = members.ToDictionary(member => member, Callees, StringComparer.Ordinal);
            var seeds = members.Where(member => !members.Any(other => other != member && callees[other].Contains(member))).ToHashSet(StringComparer.Ordinal);
            var covered = seeds.SelectMany(seed => callees[seed].Append(seed)).ToHashSet(StringComparer.Ordinal);
            seeds.UnionWith(members.Where(member => !covered.Contains(member)));
            var instances = new List<string>();
            _constructions.Add(container, instances);
            foreach (var member in members)
            {
                if (!seeds.Contains(member) || _program.Method(member) is not { } method)
                    continue;
                var instance = Instance(member, STARTUP_CONTEXT, NO_SUBSTITUTION, [], [], !method.IsStatic);
                if (instance is null)
                    continue;
                instances.Add(instance.Id);
                for (var ordinal = 0; ordinal < method.Parameters.Count; ordinal++)
                {
                    var parameter = method.Parameters[ordinal];
                    if (parameter.TypeKey.EndsWith(SERVICE_COLLECTION_TYPE, StringComparison.Ordinal))
                    {
                        Add(Parameter(instance, ordinal), [container]);
                        continue;
                    }

                    var display = $"param:{method.DisplaySymbol}#{parameter.Name}";
                    var symbolic = Region($"param|{member}|{ordinal}", HeapRegionKind.Symbolic, display, parameter.TypeKey, STARTUP_CONTEXT, null);
                    Uncertain(symbolic, $"{display}: value comes from an unknown caller of {method.DisplaySymbol}.");
                    Add(Parameter(instance, ordinal), [symbolic]);
                }
            }
        }

        private const string STARTUP_CONTEXT = "startup";

        private IEnumerable<string> BodiesOf(string member) => [member, .. _program.Method(member)?.NestedBodyIds ?? []];

        /// <summary>Every reachable member or local function a member's calls reach, transitively.</summary>
        private HashSet<string> Callees(string member)
        {
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>([member]);
            while (pending.TryDequeue(out var current))
            {
                foreach (var call in BodiesOf(current).SelectMany(body => _scope.Summaries.Get(body)?.Calls ?? []))
                {
                    if (_scope.Reachable.Bodies.ContainsKey(call.Target) && reached.Add(call.Target))
                        pending.Enqueue(call.Target);
                }
            }

            return reached;
        }

        private void Seed()
        {
            SeedStartup();
            foreach (var root in _scope.Roots.OrderBy(root => root.StableRootId, StringComparer.Ordinal))
            {
                var bindings = root.InstanceBindings;
                var invocation = $"root:{root.StableRootId}";
                var context = invocation;
                string? receiver = null;
                ResolutionScope scope = new(root.StableRootId);
                if (bindings is { Receiver: ReceiverKind.HostedService, ReceiverTypeKey: { } hostedKey })
                {
                    scope = new ResolutionScope(null);
                    var hosted = _scope.DiIndex.HostedServices.FirstOrDefault(candidate => candidate.ImplementationTypeKey == hostedKey);
                    receiver = Region($"{HeapIdentity(DiIndex.HOSTED_SERVICE_KEY, hostedKey, DiLifetime.Singleton, 1)}|singleton", HeapRegionKind.Di,
                                      DiIndex.RegionDisplay(bindings.ReceiverType ?? DisplayType(hostedKey), DiLifetime.Singleton, 1), hostedKey,
                                      "singleton", null, mayOverlapItself: hosted?.InstanceCount == HostedServiceInstanceCount.Unknown);
                    Construct(receiver, hostedKey, scope);
                }
                else if (bindings is { Receiver: ReceiverKind.PerInvocation, ReceiverTypeKey: { } controllerKey })
                {
                    receiver = Region($"receiver|{controllerKey}|{invocation}", HeapRegionKind.Receiver, $"receiver:{DisplayType(controllerKey)}",
                                      controllerKey, invocation, null);
                    Construct(receiver, controllerKey, scope);
                }
                else if (bindings is { Receiver: ReceiverKind.DiService, ReceiverTypeKey: { } serviceKey })
                {
                    receiver = Resolve(_scope.DiIndex.Resolve(serviceKey), scope, invocation, root.Entry.Symbol, "this").FirstOrDefault();
                }

                var bodyId = root.Entry.BodyKey;
                var memberId = _memberOfNestedBody.GetValueOrDefault(bodyId);
                var method = _program.Method(memberId ?? bodyId);
                var substitution = receiver is not null && method is not null ? ReceiverSubstitution(_regions[receiver], method, []) : NO_SUBSTITUTION;
                var entry = Instance(bodyId, receiver ?? context, substitution, receiver is null ? [] : [receiver],
                                     memberId is null ? [] : [invocation], receiver is null && method is { IsStatic: false });
                if (entry is null)
                    continue;
                _rootInstances[root.StableRootId] = entry.Id;
                if (receiver is not null && scope.InvocationRootId is not null)
                    _regionTriggers.Add((entry.Id, -1, receiver));
                if (scope.InvocationRootId is { } request)
                    Add(entry.Requests, [request]);

                for (var ordinal = 0; ordinal < bindings.Parameters.Count; ordinal++)
                {
                    var parameter = bindings.Parameters[ordinal];
                    if (IsServiceProvider(parameter.TypeKey) || parameter.Type is "System.IServiceProvider" or "IServiceProvider")
                    {
                        Add(Parameter(entry, ordinal), [Provider(scope)]);
                    }
                    else if (parameter is { Kind: ParameterBindingKind.DiService, IsValueType: false, TypeKey: { } typeKey })
                    {
                        var resolved = Resolve(_scope.DiIndex.Resolve(typeKey), scope, invocation, root.Entry.Symbol, parameter.Name);
                        Inject(Parameter(entry, ordinal), resolved);
                        Trigger(entry, -1, resolved);
                    }
                }
            }
        }

        private static bool IsServiceProvider(string? typeKey) => typeKey?.EndsWith(SERVICE_PROVIDER_TYPE, StringComparison.Ordinal) == true;

        /// <summary>The region standing for a scope's <see cref="IServiceProvider"/>.</summary>
        private string Provider(ResolutionScope scope)
        {
            var provider = Region($"provider|{scope.Context}", HeapRegionKind.Provider, $"provider:{scope.Context}", null, scope.Context, null);
            _providers.TryAdd(provider, scope);
            return provider;
        }

        /// <summary>Binds a target to the objects of the resolved regions, now and in every later round, since a factory or instance
        /// registration's object grows with the fixpoint.</summary>
        private void Inject(HashSet<string> target, IEnumerable<string> regions)
        {
            foreach (var region in regions)
            {
                _sinks.Add((target, region));
                Add(target, Object(region));
            }
        }

        /// <summary>The objects a resolved region stands for: the region itself, or a factory or instance registration's object.</summary>
        private IReadOnlyCollection<string> Object(string region)
        {
            if (!_registrations.TryGetValue(region, out var state))
                return [region];

            var objects = new HashSet<string>(StringComparer.Ordinal);
            if (state.Registration.Form == DiRegistrationForm.Factory)
            {
                foreach (var instance in state.Instances)
                    objects.UnionWith(_instances[instance].Returns);
            }
            else if (Site(state.Registration) is { } site && LastArgument(site.Call) is { } argument)
            {
                foreach (var holder in _instances.Values.Where(instance => instance.BodyId == site.BodyId).ToArray())
                    objects.UnionWith(Eval(holder, argument.Values));
            }

            if (state.Defaulted)
                objects.Add(region);
            return objects;
        }

        private static CallArgument? LastArgument(SummaryOpaqueCall call) => call.Arguments.OrderBy(argument => argument.ParameterOrdinal).LastOrDefault();

        /// <summary>The body and opaque call of a registration's recorded call.</summary>
        private (string BodyId, SummaryOpaqueCall Call)? Site(DiRegistration registration)
        {
            var key = (registration.BodyId!, registration.OperationId!.Value);
            if (_sites.TryGetValue(key, out var site))
                return site;

            site = null;
            foreach (var body in BodiesOf(registration.BodyId!).Where(_scope.Reachable.Bodies.ContainsKey))
            {
                if (_scope.Summaries.Get(body)?.OpaqueCalls.FirstOrDefault(call => call.OperationId == key.Item2 &&
                                                                                   call.Callee.Contains(registration.Method, StringComparison.Ordinal))
                        is { } call)
                {
                    site = (body, call);
                    break;
                }
            }

            _sites.Add(key, site);
            return site;
        }

        /// <summary>Runs a reached factory registration's delegate targets as the construction of its region: a nested body with the
        /// creating instance's cells, a static method, or an instance method on each captured receiver, each in the region's context,
        /// with the provider parameter standing for the triggering scope.</summary>
        private void ConstructFactory(RegistrationState state)
        {
            if (state.Registration.Form != DiRegistrationForm.Factory || Site(state.Registration) is not { } site || LastArgument(site.Call) is not { } argument)
                return;

            var delegates = _instances.Values.Where(instance => instance.BodyId == site.BodyId).ToArray()
                                      .SelectMany(holder => Eval(holder, argument.Values))
                                      .Where(_delegates.ContainsKey)
                                      .Distinct(StringComparer.Ordinal)
                                      .ToArray();
            foreach (var region in delegates)
            {
                var target = _delegates[region];
                foreach (var instance in FactoryInstances(state.Region, target))
                {
                    _factoryInstances.TryAdd(instance.Id, state.Region);
                    if (state.Instances.Add(instance.Id))
                        _changes++;
                    if (!_constructions.TryGetValue(state.Region, out var constructors))
                        _constructions.Add(state.Region, constructors = []);
                    if (!constructors.Contains(instance.Id))
                        constructors.Add(instance.Id);
                    Add(Parameter(instance, 0), [Provider(state.Scope)]);
                    if (state.Scope.InvocationRootId is { } request)
                        Add(instance.Requests, [request]);
                }
            }
        }

        private IEnumerable<InstanceState> FactoryInstances(string region, DelegateState target)
        {
            if (target.IsNestedBody)
            {
                var owner = target.CellOwners.Select(id => _instances.GetValueOrDefault(id)).OfType<InstanceState>().FirstOrDefault();
                if (Instance(target.Target, region, target.OwnerSubstitution, target.CapturedReceivers, target.CellOwners, owner?.IsReceiverless ?? false)
                    is { } nested)
                {
                    yield return nested;
                }

                yield break;
            }

            if (_program.Method(target.Target) is not { } method)
                yield break;
            if (method.IsStatic)
            {
                if (Instance(method.MethodId, region, Substitution(method, target.ContainingTypeKey, target.MethodTypeArguments), [], [], false) is { } factory)
                    yield return factory;
                yield break;
            }

            foreach (var receiver in target.CapturedReceivers.ToArray())
            {
                var implementation = _regions[receiver].TypeKey is { } typeKey && _program.Implementation(typeKey, method.MethodId) is { HasSourceBody: true } found
                    ? found
                    : method;
                if (Instance(implementation.MethodId, $"{region}|{receiver}", ReceiverSubstitution(_regions[receiver], implementation, target.MethodTypeArguments),
                             [receiver], [], false) is { } factory)
                {
                    yield return factory;
                }
            }
        }

        /// <summary>The region a DI resolution gives: the bound registration's region, or the scope's provider for
        /// <see cref="IServiceProvider"/>.</summary>
        private IReadOnlyList<string> Resolve(DiResolution resolution, ResolutionScope scope, string resolver, string consumer, string parameter)
        {
            if (IsServiceProvider(resolution.ServiceType))
                return [Provider(scope)];
            if (resolution is not { Kind: DiResolutionKind.Bound, Binding: { } binding })
                return [];

            var registration = resolution.Registrations.LastOrDefault(candidate =>
                                   DiIndex.RegionId(resolution.ServiceType, candidate.ImplementationTypeKey!, candidate.Lifetime, candidate.Number) == binding.RegionId)
                               ?? resolution.Registrations[^1];
            return [Registration(resolution.ServiceType, registration, scope, resolver, consumer, parameter)];
        }

        /// <summary>A registration's identity in the heap: its index identity, the number shown only from the second identical
        /// registration on, as the display shows it.</summary>
        private static string HeapIdentity(string serviceTypeKey, string implementationTypeKey, DiLifetime lifetime, int number) =>
            number < 2 ? $"di|{serviceTypeKey}|{implementationTypeKey}@{lifetime}" : DiIndex.RegionId(serviceTypeKey, implementationTypeKey, lifetime, number);

        private static IEnumerable<DiRegistration> Supported(DiResolution resolution) =>
            resolution.Registrations.Where(registration => registration is { IsSupported: true, IsHostedService: false, ImplementationTypeKey: not null });

        /// <summary>The region of one registration: one object for a singleton; per invocation, per created scope, or one root-scope
        /// object, for a scoped service; per resolving object, consumer and parameter for a transient. A new region with a type
        /// registration is constructed; a factory or instance registration's region is recorded for its object.</summary>
        private string Registration(string serviceTypeKey, DiRegistration registration, ResolutionScope scope, string resolver, string consumer,
                                    string parameter)
        {
            var context = registration.Lifetime switch
            {
                DiLifetime.Singleton => "singleton",
                DiLifetime.Scoped => scope.Context,
                _ => $"{resolver}|{consumer}|{parameter}"
            };
            var identity = $"{HeapIdentity(serviceTypeKey, registration.ImplementationTypeKey!, registration.Lifetime, registration.Number)}|{context}";
            if (_regions.ContainsKey(identity))
                return identity;

            Region(identity, HeapRegionKind.Di, DiIndex.RegionDisplay(registration.ImplementationType!, registration.Lifetime, registration.Number),
                   registration.ImplementationTypeKey, context, null);
            var constructionScope = registration.Lifetime == DiLifetime.Singleton ? new ResolutionScope(null) : scope;
            if (registration.Form == DiRegistrationForm.Type)
            {
                Construct(identity, registration.ImplementationTypeKey!, constructionScope);
            }
            else
            {
                _registrations.Add(identity, new RegistrationState(identity, registration, constructionScope));
                if (registration.Form == DiRegistrationForm.Instance)
                    _startupRegions.Add(identity);
            }

            return identity;
        }

        /// <summary>Runs a region's selected constructor with the region as <c>this</c> and its parameters bound to their resolved DI
        /// regions, once, and activates the built type's type initializer.</summary>
        private void Construct(string regionId, string typeKey, ResolutionScope scope)
        {
            if (_constructions.ContainsKey(regionId))
                return;
            var constructorInstances = new List<string>();
            _constructions.Add(regionId, constructorInstances);

            var type = _program.Type(typeKey);
            var bindings = type is null ? null : _scope.InjectionBindings.FirstOrDefault(candidate => candidate.TypeKey == type.TypeKey);
            foreach (var constructor in ReachableSet.SelectedConstructors(_program, typeKey, bindings))
            {
                var instance = Instance(constructor.MethodId, regionId, ReceiverSubstitution(_regions[regionId], constructor, []), [regionId], [], false);
                if (instance is null)
                    continue;
                constructorInstances.Add(instance.Id);
                if (scope.InvocationRootId is { } request)
                    Add(instance.Requests, [request]);
                foreach (var parameter in bindings?.ConstructorParameters ?? [])
                {
                    if (parameter.Ordinal < constructor.Parameters.Count && constructor.Parameters[parameter.Ordinal].TypeKey == parameter.TypeKey)
                    {
                        var resolution = ReachableSet.ResolveConstructorParameter(_program, _scope.DiIndex, typeKey, parameter);
                        var resolved = Resolve(resolution, scope, regionId, typeKey, parameter.Name);
                        Inject(Parameter(instance, parameter.Ordinal), resolved);
                        Trigger(instance, -1, resolved);
                        // A created scope's objects have no execution of their own: their constructions run where the scope's owner runs.
                        foreach (var child in resolved.Where(child => child.Contains("|scope:", StringComparison.Ordinal)))
                            ConstructionEdges(instance, -1, child);
                    }
                }
            }

            ActivateTypeInitializer(typeKey, null, regionId);
        }

        private InstanceState? Instance(string bodyId, string context, IReadOnlyDictionary<string, string> substitution,
                                        IEnumerable<string> receivers, IEnumerable<string> cellOwners, bool receiverless,
                                        bool merged = false)
        {
            if (!_scope.Reachable.Bodies.ContainsKey(bodyId))
                return null;

            merged = merged || _mergedBodies.Contains(bodyId) || substitution.Values.Any(_program.IsOpen);
            var key = $"{bodyId}@{context}{SubstitutionText(substitution)}";
            if (!merged && !_instances.ContainsKey(key))
            {
                var count = _contextCounts.GetValueOrDefault(bodyId);
                if (count >= _limits.MaxContextsPerMethod)
                {
                    _mergedBodies.Add(bodyId);
                    merged = true;
                }
                else
                {
                    _contextCounts[bodyId] = count + 1;
                }
            }

            if (merged)
                key = $"{bodyId}@merged";

            if (!_instances.TryGetValue(key, out var instance))
            {
                var summary = _scope.Summaries.Get(bodyId);
                if (summary is null)
                    return null;
                instance = new InstanceState(key, bodyId, merged ? "merged" : context, merged ? NO_SUBSTITUTION : substitution, merged,
                                             !merged && receiverless, summary);
                _instances.Add(key, instance);
                _instanceOrder.Add(key);
                _changes++;
                if (merged)
                    _counters[HeapCounters.MERGED_CONTEXT] = _counters.GetValueOrDefault(HeapCounters.MERGED_CONTEXT) + 1;
                if (!_memberOfNestedBody.ContainsKey(bodyId))
                    instance.CellOwners.Add(key);
                if (_instanceRegistrationsByMember.TryGetValue(_memberOfNestedBody.GetValueOrDefault(bodyId) ?? bodyId, out var registrations))
                    MapRegisteredAllocations(instance, registrations);
            }

            Add(instance.Receivers, receivers);
            Add(instance.CellOwners, cellOwners);
            return instance;
        }

        /// <summary>The objects an instance registration's argument allocates in the registration body are the registration's own
        /// region, constructed there at startup.</summary>
        private void MapRegisteredAllocations(InstanceState instance, IReadOnlyList<DiRegistration> registrations)
        {
            foreach (var registration in registrations)
            {
                if (Site(registration) is not { } site || site.BodyId != instance.BodyId || LastArgument(site.Call) is not { } argument)
                    continue;
                var region = Registration(registration.ServiceTypeKey!, registration, new ResolutionScope(null), "", "", "");
                foreach (var allocation in argument.Values.OfType<AllocationValue>())
                    _registeredAllocations.TryAdd((allocation.Site.BodyId, allocation.Site.OperationId, ContextKey(instance)), region);
            }
        }

        private static string SubstitutionText(IReadOnlyDictionary<string, string> substitution) =>
            substitution.Count == 0 ? "" : "|" + string.Join(";", substitution.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                                                              .Select(pair => $"{pair.Key}={pair.Value}"));

        private void Process(InstanceState instance)
        {
            var summary = instance.Summary;
            var memberId = _memberOfNestedBody.GetValueOrDefault(instance.BodyId) ?? instance.BodyId;
            var capturedKeys = CapturedKeys(memberId);
            foreach (var variable in summary.Variables.Where(variable => capturedKeys.Contains(variable.SymbolKey)))
            {
                var values = Eval(instance, variable.Values);
                foreach (var owner in instance.CellOwners.ToArray())
                    Add(Cell(owner, variable.SymbolKey), values);
            }

            foreach (var store in summary.CapturedStores)
            {
                var values = Eval(instance, store.Values);
                foreach (var owner in instance.CellOwners.ToArray())
                    Add(Cell(owner, store.SymbolKey), values);
            }

            foreach (var store in summary.Stores)
            {
                var values = Eval(instance, store.Values);
                if (store.Field.IsStatic)
                    Add(Field(StaticRegion(instance, store.Field), FieldSlot.Key(store.Field)), values);
                else
                    foreach (var @base in Eval(instance, store.Bases))
                        Add(Field(@base, FieldSlot.Key(store.Field)), values);
            }

            foreach (var element in summary.Elements.Where(element => element.Kind == ElementOperationKind.Store))
            {
                var values = Eval(instance, element.Values);
                foreach (var array in Eval(instance, element.Arrays))
                    Add(Field(array, PathValue.ELEMENT), values);
            }

            foreach (var @return in summary.Returns)
            {
                Add(instance.Returns, Eval(instance, @return.Values));
                if (!instance.ReturnsUnfollowed && Unfollowed(instance, @return.UnknownSources, @return.SourceCalls))
                {
                    instance.ReturnsUnfollowed = true;
                    _changes++;
                }
            }
            foreach (var parameter in summary.RefParameters)
                Add(RefParameter(instance, parameter.Ordinal), Eval(instance, parameter.Values));

            foreach (var created in summary.Delegates)
            {
                var region = DelegateRegion(instance, created.Delegate);
                var state = _delegates[region];
                Add(state.CellOwners, instance.CellOwners);
                state.CapturedKeys.UnionWith(created.Delegate.CapturedValues.Keys.Where(key => key != "this"));
                if (created.Delegate.CapturedValues.TryGetValue("this", out var thisValues))
                    Add(state.CapturedReceivers, Eval(instance, thisValues));
                if (!state.IsNestedBody && _program.Method(state.Target) is { IsStatic: true } method)
                    ActivateTypeInitializer(state.ContainingTypeKey ?? method.ContainingTypeKey, instance, null);
            }

            foreach (var access in summary.Accesses.Where(access => access.Field.IsStatic))
            {
                StaticRegion(instance, access.Field);
                ActivateTypeInitializer(StaticTypeKey(instance, access.Field), instance, null);
            }

            foreach (var call in summary.Calls)
                Call(instance, call);
            foreach (var call in summary.OpaqueCalls)
                Locate(instance, call);

            foreach (var threadWork in summary.ThreadWorks)
            {
                var work = Eval(instance, threadWork.Work.Values);
                foreach (var thread in Eval(instance, threadWork.Thread.Values))
                    Add(Field(thread, THREAD_WORK_FIELD), work);
            }

            foreach (var spawn in summary.Spawns)
                Spawn(instance, spawn);
            foreach (var timer in summary.Timers.Where(timer => timer.Callback is not null))
                TimerCallback(instance, timer);
            // What an unresolved call is handed it may run whenever it likes (R3); a call a recognizer or the table models is no such call.
            var modelled = _modelled ??= new UnknownCalls.Modelled(_scope);
            foreach (var call in summary.OpaqueCalls.Where(call => !call.IsKnown && !modelled.Contains(instance.BodyId, call)))
                Handoff(instance, call.OperationId, call.Arguments.SelectMany(argument => argument.Values).Concat(call.Delegates));
            foreach (var dynamic in summary.DynamicOperations)
                Handoff(instance, dynamic.OperationId, dynamic.Values);
            foreach (var whenAll in summary.WhenAlls)
                WhenAll(instance, whenAll);
            foreach (var unwrap in summary.Unwraps)
                Add(CallResult(instance, unwrap.CallOperationId), Tails(Eval(instance, unwrap.Outer.Values)));
        }

        /// <summary>Runs a spawn's work: the delegates it names, the work bound to a started thread, or the <c>Execute()</c> of each
        /// work item object. A handle is a region of the site in the caller's context; <c>StartNew</c>, <c>ContinueWith</c> and a
        /// <c>Task.Run</c> overload that does not wait for its work's task also have a tail once a resolved callee's body is async. The work gets its state, a continuation its
        /// antecedent, and a <c>Parallel</c> loop's body and <c>localFinally</c> the values <c>localInit</c> and the body return.</summary>
        private void Spawn(InstanceState caller, SummarySpawn spawn)
        {
            var site = SiteOf(_spawns, caller, spawn.OperationId, () => new SiteState(spawn.Kind, spawn.CallOperationId));
            IReadOnlyList<HashSet<string>> works;
            string? handle = null;
            if (spawn.Kind == IrSpawnKind.ThreadStart)
            {
                var threads = Eval(caller, spawn.Handle!.Values);
                Add(site.Handles, threads);
                works = [threads.SelectMany(thread => Load(thread, THREAD_WORK_FIELD)).ToHashSet(StringComparer.Ordinal)];
            }
            else
            {
                works = spawn.Work.Select(work => Eval(caller, work.Values)).ToArray();
                if (spawn.Handle is not null)
                {
                    handle = TaskRegion(caller, spawn.CallOperationId);
                    Add(site.Handles, [handle]);
                    Add(CallResult(caller, spawn.CallOperationId), [handle]);
                    if (spawn.Antecedent is { } antecedent)
                        Add(Get(_antecedents, handle), Eval(caller, antecedent.Values));
                }
            }

            var callees = works.Select(regions => spawn.WorkMethod is { } workMethod
                                                      ? WorkItemCallees(regions, workMethod)
                                                      : regions.Where(_delegates.ContainsKey)
                                                               .SelectMany(region => DelegateCallees(caller, spawn.OperationId, _delegates[region], () => { }))
                                                               .ToList())
                               .ToArray();
            if (handle is not null && site.Tail is null &&
                (spawn.Kind is IrSpawnKind.StartNew or IrSpawnKind.ContinueWith || spawn.Kind == IrSpawnKind.TaskRun && !spawn.AwaitsWorkTask) &&
                callees.SelectMany(list => list).Any(callee => IsAsyncBody(callee.BodyId)))
            {
                site.Tail = TailRegion(handle);
                _changes++;
            }

            var state = spawn.State is { } stateValue ? Eval(caller, stateValue.Values) : [];
            var localValues = callees.Length == 3 && spawn.Kind is IrSpawnKind.ParallelFor or IrSpawnKind.ParallelForEach
                ? callees[0].Concat(callees[1]).SelectMany(callee => callee.Returns).ToHashSet(StringComparer.Ordinal)
                : null;
            for (var index = 0; index < callees.Length; index++)
            {
                var role = localValues is null ? SpawnRole.Work : index switch { 0 => SpawnRole.LocalInit, 1 => SpawnRole.Work, _ => SpawnRole.LocalFinally };
                foreach (var callee in callees[index])
                {
                    switch (spawn.Kind)
                    {
                        case IrSpawnKind.ContinueWith:
                            BindParameter(callee, 0, Eval(caller, spawn.Antecedent!.Values));
                            BindParameter(callee, 1, state);
                            break;
                        case IrSpawnKind.StartNew or IrSpawnKind.QueueUserWorkItem or IrSpawnKind.UnsafeQueueUserWorkItem or IrSpawnKind.ThreadStart:
                            BindParameter(callee, 0, state);
                            break;
                    }

                    if (localValues is not null && role != SpawnRole.LocalInit)
                        BindParameter(callee, role == SpawnRole.Work ? ParameterCount(callee) - 1 : 0, localValues);
                    Add(callee.Requests, caller.Requests);
                    if (site.Callees.Add((callee.Id, role)))
                        _changes++;
                }
            }
        }

        private bool IsAsyncBody(string bodyId) =>
            _scope.Reachable.Bodies.TryGetValue(bodyId, out var body) && body is { IsAsync: true, IsAsyncIterator: false };

        /// <summary>The <c>Execute()</c> implementations a work item region runs, with the region as receiver.</summary>
        private List<InstanceState> WorkItemCallees(IEnumerable<string> items, string workMethod)
        {
            var callees = new List<InstanceState>();
            if (ReachableSet.WorkMethodId(_program, workMethod) is not { } methodId)
                return callees;
            foreach (var item in items)
                callees.AddRange(DispatchCallees(item, methodId, []).Callees);
            return callees;
        }

        /// <summary>Runs a timer's callback or <c>Elapsed</c> handler; a created timer's callback gets the timer's state.</summary>
        private void TimerCallback(InstanceState caller, SummaryTimer timer)
        {
            var site = SiteOf(_timerCallbacks, caller, timer.OperationId, () => new SiteState(null, timer.OperationId, timer.Action));
            Add(site.Handles, Eval(caller, timer.Timer.Values));
            var state = timer.State is { } stateValue ? Eval(caller, stateValue.Values) : [];
            foreach (var region in Eval(caller, timer.Callback!.Values).Where(_delegates.ContainsKey))
            {
                foreach (var callee in DelegateCallees(caller, timer.OperationId, _delegates[region], () => { }))
                {
                    if (timer.Action == IrTimerAction.Create)
                        BindParameter(callee, 0, state);
                    Add(callee.Requests, caller.Requests);
                    if (site.Callees.Add((callee.Id, SpawnRole.Work)))
                        _changes++;
                }
            }
        }

        /// <summary>A <c>WhenAll</c> result is a region of its site whose group remembers the listed tasks, or that they are unknown.</summary>
        private void WhenAll(InstanceState caller, SummaryWhenAll whenAll)
        {
            var group = TaskRegion(caller, whenAll.CallOperationId);
            Add(CallResult(caller, whenAll.CallOperationId), [group]);
            if (!_taskGroups.TryGetValue(group, out var members))
            {
                _taskGroups.Add(group, members = (new HashSet<string>(StringComparer.Ordinal), whenAll.TasksKnown));
                _changes++;
            }

            Add(members.Members, whenAll.Tasks.SelectMany(task => Eval(caller, task.Values)).ToArray());
        }

        /// <summary>Whether the regions of a timer callback site name every timer it may run. As in <c>TimerSteps</c>, a null or a field
        /// read before its first write is no timer, and a source call names one only when what it returned is among those regions: a call
        /// with no resolved implementation, or one returning a value the heap does not follow, may return any timer.</summary>
        private bool TimersKnown(string callerId, int operationId, IReadOnlySet<string> timers)
        {
            var caller = _instances[callerId];
            var timer = caller.Summary.Timers.First(step => step.OperationId == operationId).Timer;
            return !Unfollowed(caller, timer.UnknownSources, timer.SourceCalls) &&
                   timer.SourceCalls.All(call => CallResult(caller, call) is { Count: > 0 } result && result.All(timers.Contains));
        }

        private SiteState SiteOf(Dictionary<(string Caller, int Operation), SiteState> sites, InstanceState caller, int operationId,
                                 Func<SiteState> create)
        {
            if (!sites.TryGetValue((caller.Id, operationId), out var site))
            {
                sites.Add((caller.Id, operationId), site = create());
                _changes++;
            }

            return site;
        }

        /// <summary>Binds a parameter of a spawned or called-back body, when the body has it.</summary>
        private void BindParameter(InstanceState callee, int ordinal, IEnumerable<string> regions)
        {
            if (ordinal >= 0 && ordinal < ParameterCount(callee))
                Add(Parameter(callee, ordinal), regions);
        }

        private int ParameterCount(InstanceState instance) =>
            _scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) ? body.Parameters.Count : 0;

        /// <summary>The handle region an operation of an instance creates: a spawn's task, an async call's task or a
        /// <c>WhenAll</c> result, one per site and context.</summary>
        private string TaskRegion(InstanceState instance, int operationId)
        {
            var owner = _scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) ? body.OwnerSymbol : instance.BodyId;
            return Region($"task|{instance.BodyId}#{operationId}|{ContextKey(instance)}", HeapRegionKind.Task, $"task:{owner}#{operationId}", null,
                          instance.Context, $"task|{instance.BodyId}#{operationId}", merged: instance.IsMerged,
                          site: new CreationSite(instance.BodyId, operationId, "", 0));
        }

        private string TailRegion(string handle)
        {
            if (_tails.TryGetValue(handle, out var tail))
                return tail;
            var region = _regions[handle];
            tail = Region($"{handle}|tail", HeapRegionKind.Task, $"{region.Display}#tail", null, region.Context, $"{region.Group}|tail",
                          merged: region.IsMerged, site: new CreationSite(region.SiteBodyId!, region.SiteOperationId!.Value, "", 0));
            _tails.Add(handle, tail);
            return tail;
        }

        private IEnumerable<string> Tails(IEnumerable<string> handles) => handles.Select(_tails.GetValueOrDefault).OfType<string>();

        /// <summary>Models a service call: a created scope is an allocation of the calling execution whose <c>ServiceProvider</c>
        /// stands for it; a locator call with a constant type resolves in each scope its receiver stands for, and
        /// <c>GetServices</c> gives a collection of every supported registration's object.</summary>
        private void Locate(InstanceState instance, SummaryOpaqueCall call)
        {
            if (call.ServiceCall is not { } service)
            {
                if (call.Callee.Contains("ServiceProvider", StringComparison.Ordinal))
                {
                    var scopes = Eval(instance, call.Receivers).Where(_scopeObjects.Contains).ToArray();
                    Add(CallResult(instance, call.OperationId), scopes.Select(scope => Provider(new ResolutionScope(null, scope))));
                }

                return;
            }

            var owner = _scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) ? body.OwnerSymbol : instance.BodyId;
            var site = new CreationSite(instance.BodyId, call.OperationId, "", 0);
            if (service.Kind == IrServiceCallKind.ScopeCreation)
            {
                var created = Region($"scope|{instance.BodyId}#{call.OperationId}|{ContextKey(instance)}", HeapRegionKind.Allocation,
                                     $"scope:{owner}#{call.OperationId}", null, instance.Context, $"scope|{instance.BodyId}#{call.OperationId}",
                                     merged: instance.IsMerged, site: site);
                _scopeObjects.Add(created);
                Add(CallResult(instance, call.OperationId), [created]);
                return;
            }

            if (service.ServiceTypeKey is not { } key)
                return;
            var resolution = _scope.DiIndex.Resolve(key);
            foreach (var scope in Scopes(instance, call))
            {
                var resolver = $"locator|{instance.BodyId}#{call.OperationId}";
                IReadOnlyList<string> regions = service.Kind == IrServiceCallKind.Locator
                    ? Resolve(resolution, scope, resolver, scope.Context, "")
                    : Supported(resolution).Select(registration => Registration(key, registration, scope, resolver, scope.Context, "")).ToArray();
                foreach (var region in regions.Where(region => _regions[region] is { Kind: HeapRegionKind.Di } di &&
                                                               di.Context is not ("singleton" or "root-scope") &&
                                                               !di.Context.StartsWith("invocation:", StringComparison.Ordinal)))
                {
                    ConstructionEdges(instance, call.OperationId, region);
                    if (!_locatorCreators.TryGetValue(region, out var creators))
                        _locatorCreators.Add(region, creators = new HashSet<string>(StringComparer.Ordinal));
                    Add(creators, [instance.Id]);
                }

                Trigger(instance, call.OperationId, regions);
                if (service.Kind == IrServiceCallKind.Locator)
                {
                    Inject(CallResult(instance, call.OperationId), regions);
                    continue;
                }

                var collection = Region($"services|{instance.BodyId}#{call.OperationId}|{ContextKey(instance)}|{scope.Context}", HeapRegionKind.Allocation,
                                        $"services:{owner}#{DisplayType(key)}", null, instance.Context, $"services|{instance.BodyId}#{call.OperationId}",
                                        merged: instance.IsMerged, site: site);
                if (!_collections.TryGetValue(collection, out var elements))
                    _collections.Add(collection, elements = []);
                foreach (var region in regions.Where(region => !elements.Contains(region)))
                    elements.Add(region);
                Inject(Field(collection, PathValue.ELEMENT), regions);
                Add(CallResult(instance, call.OperationId), [collection]);
            }
        }

        /// <summary>The scopes a locator call's receiver stands for: the instance's requests for <c>RequestServices</c>, the root scope
        /// for the host's and application's services, and the scope of each provider region an injected or scope provider, or a root
        /// entry's own provider parameter, points to. Any other provider stands for no scope.</summary>
        private IReadOnlyList<ResolutionScope> Scopes(InstanceState instance, SummaryOpaqueCall call)
        {
            // An extension method takes the provider as its first argument.
            var provider = call.Receivers.Count != 0
                ? call.Receivers
                : call.Arguments.OrderBy(argument => argument.ParameterOrdinal).FirstOrDefault()?.Values ?? new HashSet<AbstractValue>();
            switch (call.ServiceCall!.Provider)
            {
                case IrProviderKind.RequestServices:
                    return instance.Requests.Order(StringComparer.Ordinal).Select(request => new ResolutionScope(request)).ToArray();
                case IrProviderKind.HostServices or IrProviderKind.ApplicationServices:
                    return [new ResolutionScope(null)];
                case null when !_rootInstances.ContainsValue(instance.Id) && !_factoryInstances.ContainsKey(instance.Id) ||
                               provider.Count == 0 || !provider.All(value => value is ParameterValue):
                    return [];
                default:
                    return Eval(instance, provider).Order(StringComparer.Ordinal)
                                                         .Select(region => _providers.GetValueOrDefault(region))
                                                         .OfType<ResolutionScope>()
                                                         .ToArray();
            }
        }

        private void Trigger(InstanceState instance, int operationId, IEnumerable<string> regions)
        {
            foreach (var region in regions)
            {
                if (_regionTriggers.Add((instance.Id, operationId, region)))
                    _changes++;
            }
        }

        private void ConstructionEdges(InstanceState caller, int operationId, string region)
        {
            foreach (var constructor in _constructions.GetValueOrDefault(region)?.ToArray() ?? [])
            {
                if (_edges.Add((caller.Id, operationId, constructor, CONSTRUCTION_REASON)))
                    _changes++;
            }
        }

        private void Call(InstanceState caller, CallTransfer call)
        {
            var typeArguments = (call.TargetMethodTypeArgumentKeys ?? []).Select(key => ProgramIndex.Substitute(key, caller.Substitution)).ToArray();
            var containing = call.TargetContainingTypeKey is null ? null : ProgramIndex.Substitute(call.TargetContainingTypeKey, caller.Substitution);
            switch (call.Kind)
            {
                case IrCallKind.LocalFunction:
                {
                    var callee = Instance(call.Target, OwnerContext(caller.CellOwners), caller.Substitution, caller.Receivers, caller.CellOwners,
                                          caller.IsReceiverless);
                    Bind(caller, call, callee, "exact");
                    return;
                }
                case IrCallKind.Delegate:
                {
                    var regions = Eval(caller, call.Receivers).Where(region => _delegates.ContainsKey(region)).ToArray();
                    if (regions.Length == 0)
                    {
                        NoReceiver(caller, call);
                        _unresolvedDispatches.Add((caller.Id, call.OperationId));
                        Handoff(caller, call.OperationId, call.Arguments.SelectMany(argument => argument.Values));
                    }
                    UnresolvedTargets(caller, call);
                    foreach (var region in regions)
                        CallDelegate(caller, call, _delegates[region]);
                    return;
                }
                case IrCallKind.Virtual or IrCallKind.Interface:
                {
                    var receivers = Eval(caller, call.Receivers);
                    if (receivers.Count == 0)
                    {
                        NoReceiver(caller, call);
                        _unresolvedDispatches.Add((caller.Id, call.OperationId));
                        Handoff(caller, call.OperationId, call.Arguments.SelectMany(argument => argument.Values));
                    }
                    UnresolvedTargets(caller, call);
                    foreach (var receiver in receivers)
                        Dispatch(caller, call, call.Target, receiver, typeArguments, _regions[receiver].Kind == HeapRegionKind.Di ? "di-binding" : "points-to");
                    return;
                }
                case IrCallKind.Static:
                {
                    if (_program.Method(call.Target) is not { } method)
                        return;
                    var callee = Instance(call.Target, $"{caller.BodyId}#{call.OperationId}", Substitution(method, containing, typeArguments), [], [], false);
                    Bind(caller, call, callee, "exact");
                    ActivateTypeInitializer(containing ?? method.ContainingTypeKey, caller, null);
                    return;
                }
                default:
                {
                    if (_program.Method(call.Target) is not { } method)
                        return;
                    var receivers = Eval(caller, call.Receivers);
                    if (receivers.Count == 0)
                    {
                        NoReceiver(caller, call);
                        var callee = Instance(call.Target, $"{caller.Context}|{caller.BodyId}#{call.OperationId}",
                                              Substitution(method, containing, typeArguments), [], [], true);
                        Bind(caller, call, callee, "exact");
                    }

                    foreach (var receiver in receivers)
                    {
                        var callee = Instance(call.Target, receiver + TypeArgumentText(typeArguments),
                                              ReceiverSubstitution(_regions[receiver], method, typeArguments), [receiver], [], false);
                        Bind(caller, call, callee, "exact");
                    }

                    if (call.Kind == IrCallKind.Constructor)
                        ActivateTypeInitializer(containing ?? method.ContainingTypeKey, caller, null);
                    return;
                }
            }
        }

        /// <summary>The delegates among values an unresolved call is handed, each run by the instances of its target in an unknown
        /// execution of its own (R3, ADR 0011). A delegate handed to two such calls is one execution, which both sites started.</summary>
        private void Handoff(InstanceState caller, int operationId, IEnumerable<AbstractValue> values)
        {
            // An array handed over hands over what it holds: a params array, one created in the argument's place, or any other (R1).
            var handed = Eval(caller, values);
            handed.UnionWith(handed.Where(region => _regions[region].TypeKey?.EndsWith(']') == true)
                                   .SelectMany(array => Load(array, PathValue.ELEMENT)).ToArray());
            foreach (var region in handed.Where(_delegates.ContainsKey))
            {
                if (!_handoffs.TryGetValue(region, out var handoff))
                    _handoffs.Add(region, handoff = ([], new HashSet<string>(StringComparer.Ordinal)));
                handoff.Sites.Add((caller.Id, operationId));
                foreach (var callee in DelegateCallees(caller, operationId, _delegates[region], () => { }))
                {
                    Add(callee.Requests, caller.Requests);
                    handoff.Callees.Add(callee.Id);
                }
            }
        }

        /// <summary>A delegate invocation runs the delegate's target: its nested body with the creating instance's cells and captured
        /// receiver, a static method at the invocation site, or an instance method on each captured receiver region, resolved
        /// through the program index when the method is virtual.</summary>
        private void CallDelegate(InstanceState caller, CallTransfer call, DelegateState state)
        {
            var callees = DelegateCallees(caller, call.OperationId, state, () => NoReceiver(caller, call));
            foreach (var callee in callees)
                Bind(caller, call, callee, "delegate");
            // A method group whose target runs no body the analysis has is as unresolved as a call of that target (R1).
            if (callees.Count == 0 && !state.IsNestedBody)
            {
                var declaring = _program.Method(state.Target)?.ContainingTypeKey;
                Unresolved(caller, call, state.CapturedReceivers.Select(receiver => (receiver, declaring)).ToArray());
            }
        }

        /// <summary>Records an unresolved dispatch with the receiver objects it sees and hands off the delegates it is given (R1, R3).</summary>
        private void Unresolved(InstanceState caller, CallTransfer call, IReadOnlyCollection<(string Region, string? DeclaringTypeKey)> receivers)
        {
            _unresolvedDispatches.Add((caller.Id, call.OperationId));
            if (!_unresolvedReceivers.TryGetValue((caller.Id, call.OperationId), out var known))
                _unresolvedReceivers.Add((caller.Id, call.OperationId), known = []);
            known.UnionWith(receivers);
            Handoff(caller, call.OperationId, call.Arguments.SelectMany(argument => argument.Values));
        }

        /// <summary>The instances running a delegate's target for an operation of <paramref name="caller"/>; <paramref name="noReceiver"/>
        /// runs when an instance method has no receiver to run on.</summary>
        private List<InstanceState> DelegateCallees(InstanceState caller, int operationId, DelegateState state, Action noReceiver)
        {
            var callees = new List<InstanceState>();
            if (state.IsNestedBody)
            {
                var owner = state.CellOwners.Select(id => _instances.GetValueOrDefault(id)).OfType<InstanceState>().FirstOrDefault();
                if (Instance(state.Target, OwnerContext(state.CellOwners), state.OwnerSubstitution, state.CapturedReceivers, state.CellOwners,
                             owner?.IsReceiverless ?? false) is { } nested)
                {
                    callees.Add(nested);
                }

                return callees;
            }

            if (_program.Method(state.Target) is not { } method)
                return callees;
            if (method.IsStatic)
            {
                if (Instance(method.MethodId, $"{caller.BodyId}#{operationId}", Substitution(method, state.ContainingTypeKey, state.MethodTypeArguments),
                             [], [], false) is { } callee)
                {
                    callees.Add(callee);
                }

                ActivateTypeInitializer(state.ContainingTypeKey ?? method.ContainingTypeKey, caller, null);
                return callees;
            }

            if (state.CapturedReceivers.Count == 0)
                noReceiver();
            var isVirtual = !state.IsNonVirtual &&
                            (method.IsVirtual || method.IsAbstract || method.IsOverride || _program.Type(method.ContainingTypeKey)?.IsInterface == true);
            foreach (var receiver in state.CapturedReceivers.ToArray())
            {
                if (isVirtual)
                {
                    var dispatched = DispatchCallees(receiver, method.MethodId, state.MethodTypeArguments);
                    if (!dispatched.Dispatched)
                        noReceiver();
                    callees.AddRange(dispatched.Callees);
                    continue;
                }

                if (Instance(method.MethodId, receiver + TypeArgumentText(state.MethodTypeArguments),
                             ReceiverSubstitution(_regions[receiver], method, state.MethodTypeArguments), [receiver], [], false) is { } callee)
                {
                    callees.Add(callee);
                }
            }

            return callees;
        }

        /// <summary>A lambda or local function body instance has the context of the member-body instance owning its cells.</summary>
        private static string OwnerContext(IEnumerable<string> cellOwners) => string.Join(",", cellOwners.Order(StringComparer.Ordinal));

        private void Dispatch(InstanceState caller, CallTransfer call, string methodId, string receiver, IReadOnlyList<string> typeArguments,
                              string reason)
        {
            var (dispatched, callees) = DispatchCallees(receiver, methodId, typeArguments);
            foreach (var callee in callees)
                Bind(caller, call, callee, reason);

            // A receiver whose type has no implementation with a body runs one the analysis cannot read: however many other types do,
            // the call is unresolved for this one (R1).
            if (!dispatched)
            {
                NoReceiver(caller, call);
                var declaring = _regions[receiver].TypeKey is { } type ? _program.Implementation(type, methodId)?.ContainingTypeKey : null;
                Unresolved(caller, call, [(receiver, declaring)]);
            }
        }

        /// <summary>The source implementations a receiver region runs for a call of <paramref name="methodId"/>, and whether any of its
        /// types has one.</summary>
        private (bool Dispatched, List<InstanceState> Callees) DispatchCallees(string receiver, string methodId, IReadOnlyList<string> typeArguments)
        {
            var region = _regions[receiver];
            // A factory or instance registration's region dispatches on the types its factory or instance created, if any.
            IReadOnlyCollection<string> types = _registrations.ContainsKey(receiver)
                ? _regionTypes.GetValueOrDefault(receiver)?.Order(StringComparer.Ordinal).ToArray() ?? []
                : region.TypeKey is null ? [] : [region.TypeKey];
            var dispatched = false;
            var callees = new List<InstanceState>();
            foreach (var type in types)
            {
                if (_program.Implementation(type, methodId) is not { HasSourceBody: true } implementation)
                    continue;
                dispatched = true;
                if (Instance(implementation.MethodId, receiver + TypeArgumentText(typeArguments),
                             Substitution(implementation, _program.ConstructedBase(type, implementation.ContainingTypeKey), typeArguments),
                             [receiver], [], false) is { } callee)
                {
                    callees.Add(callee);
                }
            }

            return (dispatched, callees);
        }

        private void Bind(InstanceState caller, CallTransfer call, InstanceState? callee, string reason)
        {
            if (callee is null)
                return;
            foreach (var argument in call.Arguments)
                Add(Parameter(callee, argument.ParameterOrdinal), Eval(caller, argument.Values));
            if (_scope.Reachable.Bodies.TryGetValue(callee.BodyId, out var body) && body.IsIterator)
            {
                var region = Region($"iterator|{caller.Id}|{call.OperationId}|{callee.Id}", HeapRegionKind.Allocation,
                                    $"iterator:{call.Target}", null, ContextKey(caller), null);
                Add(CallResult(caller, call.OperationId), [region]);
                _iteratorObjects.TryAdd(region, new CallEdge(caller.Id, call.OperationId, callee.Id, reason));
            }
            else if (AsyncSpawnKind(call, callee) is { } kind)
                AsyncSpawn(caller, call.OperationId, callee, kind);
            else
            {
                Add(CallResult(caller, call.OperationId), callee.Returns);
                if (callee.ReturnsUnfollowed && caller.UnfollowedCallResults.Add(call.OperationId))
                    _changes++;
            }
            foreach (var (ordinal, values) in callee.RefParameters)
                Add(RefResult(caller, call.OperationId, ordinal), values);
            Add(callee.Requests, caller.Requests);
            if (_edges.Add((caller.Id, call.OperationId, callee.Id, reason)))
                _changes++;
        }

        /// <summary>An edge into an async body (not an async iterator) whose result the caller does not await at once starts that body
        /// as a spawn: <see cref="IrSpawnKind.AsyncVoid"/> for a <c>void</c> body, <see cref="IrSpawnKind.AsyncCall"/> otherwise, since
        /// an async body can only return <c>Task</c>, <c>ValueTask</c>, their generic forms or a type with an async method builder.</summary>
        private IrSpawnKind? AsyncSpawnKind(CallTransfer call, InstanceState callee) =>
            !call.IsAwaitedImmediately && _scope.Reachable.Bodies.TryGetValue(callee.BodyId, out var body) && body is { IsAsync: true, IsAsyncIterator: false }
                ? body.ReturnType == "void" ? IrSpawnKind.AsyncVoid : IrSpawnKind.AsyncCall
                : null;

        /// <summary>Marks an async spawn edge; an <see cref="IrSpawnKind.AsyncCall"/>'s result is its site's handle region instead of
        /// what the body returns, which is the task's value, not the task.</summary>
        private void AsyncSpawn(InstanceState caller, int operationId, InstanceState callee, IrSpawnKind kind)
        {
            var site = SiteOf(_asyncSpawns, caller, operationId, () => new SiteState(kind, operationId));
            if (kind == IrSpawnKind.AsyncCall)
            {
                var handle = TaskRegion(caller, operationId);
                Add(site.Handles, [handle]);
                Add(CallResult(caller, operationId), [handle]);
            }

            if (site.Callees.Add((callee.Id, SpawnRole.Work)))
                _changes++;
        }

        private void NoReceiver(InstanceState caller, CallTransfer call) => _noReceiver.Add((caller.BodyId, call.OperationId));

        /// <summary>Marks a dispatch whose receiver may come from an origin points-to does not follow: the bodies it resolves to are not
        /// every body it may run, so nothing that holds for all of them holds for the call.</summary>
        private void UnresolvedTargets(InstanceState caller, CallTransfer call)
        {
            if (!Unfollowed(caller, call.ReceiverUnknownSources, call.ReceiverSourceCalls))
                return;
            if (caller.UnresolvedCallTargets.Add(call.OperationId))
                _changes++;
            // What a body the heap does not have would return is not among the regions of the result either.
            if (caller.UnfollowedCallResults.Add(call.OperationId))
                _changes++;
        }

        /// <summary>The substitution of a method running on a receiver region: the region's type projected onto the method's declaring
        /// type, plus the call's method type arguments.</summary>
        private IReadOnlyDictionary<string, string> ReceiverSubstitution(HeapRegion receiver, ProgramMethod method, IReadOnlyList<string> typeArguments) =>
            Substitution(method, receiver.TypeKey is null ? null : _program.ConstructedBase(receiver.TypeKey, method.ContainingTypeKey), typeArguments);

        private IReadOnlyDictionary<string, string> Substitution(ProgramMethod method, string? containingTypeKey, IReadOnlyList<string> typeArguments)
        {
            var substitution = new Dictionary<string, string>(StringComparer.Ordinal);
            if (containingTypeKey is not null && _program.Type(method.ContainingTypeKey) is { TypeParameterKeys.Count: > 0 } declaring)
            {
                var (_, arguments) = _program.Decompose(containingTypeKey);
                foreach (var (parameter, argument) in declaring.TypeParameterKeys.Zip(arguments))
                {
                    if (parameter != argument)
                        substitution[parameter] = argument;
                }
            }

            foreach (var (parameter, argument) in method.TypeParameterKeys.Zip(typeArguments))
            {
                if (parameter != argument)
                    substitution[parameter] = argument;
            }

            return substitution.Count == 0 ? NO_SUBSTITUTION : substitution;
        }

        private static string TypeArgumentText(IReadOnlyList<string> typeArguments) =>
            typeArguments.Count == 0 ? "" : $"<{string.Join(",", typeArguments)}>";

        /// <summary>Activates a closed type's type-initializer construction once, recording what triggered it; a reference from the
        /// type initializer itself is not a trigger.</summary>
        private void ActivateTypeInitializer(string typeKey, InstanceState? triggerInstance, string? triggerRegion)
        {
            if (_program.Type(typeKey) is not { } type ||
                _program.MethodsOf(type.TypeKey).FirstOrDefault(method => method is { Kind: ProgramMethodKind.TypeInitializer, HasSourceBody: true }) is not { } initializer ||
                triggerInstance?.BodyId == initializer.MethodId)
            {
                return;
            }

            if (!_typeInitializers.TryGetValue(typeKey, out var construction))
            {
                var (_, arguments) = _program.Decompose(typeKey);
                var substitution = type.TypeParameterKeys.Zip(arguments).Where(pair => pair.First != pair.Second)
                                       .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
                // An open type's one construction stands for every closed one, so its regions are merged.
                var instance = Instance(initializer.MethodId, $"type-initializer:{typeKey}", substitution, [], [], false,
                                        merged: _program.IsOpen(typeKey));
                construction = (instance?.Id ?? "", new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                _typeInitializers.Add(typeKey, construction);
                _changes++;
            }

            if (triggerInstance is not null && construction.Instances.Add(triggerInstance.Id))
                _changes++;
            if (triggerRegion is not null && construction.Regions.Add(triggerRegion))
                _changes++;
        }

        private HashSet<string> Eval(InstanceState instance, IEnumerable<AbstractValue> values)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
                result.UnionWith(Eval(instance, value));
            return result;
        }

        private HashSet<string> Eval(InstanceState instance, AbstractValue value) => value switch
        {
            ThisValue => new HashSet<string>(instance.Receivers, StringComparer.Ordinal),
            ParameterValue parameter => new HashSet<string>(Parameter(instance, parameter.Ordinal), StringComparer.Ordinal),
            StaticFieldValue field => Load(StaticRegion(instance, field.Field), FieldSlot.Key(field.Field)),
            AllocationValue allocation => [AllocationRegion(instance, allocation.Site)],
            CallResultValue result => new HashSet<string>(CallResult(instance, result.OperationId), StringComparer.Ordinal),
            RefResultValue result => new HashSet<string>(RefResult(instance, result.OperationId, result.Ordinal), StringComparer.Ordinal),
            DelegateCreationValue created => [DelegateRegion(instance, created)],
            CapturedValue captured => instance.CellOwners.SelectMany(owner => Cell(owner, captured.SymbolKey)).ToHashSet(StringComparer.Ordinal),
            AwaitResultValue awaited => Tails(instance.Summary.Joins.Where(join => join.OperationId == awaited.OperationId)
                                                      .SelectMany(join => join.Handles)
                                                      .SelectMany(handle => Eval(instance, handle.Values)))
                                            .ToHashSet(StringComparer.Ordinal),
            PathValue path => EvalPath(instance, path),
            _ => new HashSet<string>(StringComparer.Ordinal)
        };

        private HashSet<string> EvalPath(InstanceState instance, PathValue path)
        {
            var current = Eval(instance, path.Base);
            foreach (var segment in path.Segments)
            {
                var next = new HashSet<string>(StringComparer.Ordinal);
                foreach (var region in current)
                {
                    if (segment == PathValue.WILDCARD)
                        next.UnionWith(Closure(region));
                    else
                        next.UnionWith(Load(region, segment));
                }

                current = next;
            }

            return current;
        }

        /// <summary>Every region reachable from a region through any fields.</summary>
        private HashSet<string> Closure(string region)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([region]);
            while (pending.TryPop(out var current))
            {
                foreach (var pair in _fields.Where(pair => pair.Key.Region == current).ToArray())
                {
                    foreach (var target in pair.Value)
                    {
                        if (seen.Add(target))
                            pending.Push(target);
                    }
                }
            }

            return seen;
        }

        /// <summary>A field slot of a region, or, for a bare field name, every declaring type's slot of that name.</summary>
        private HashSet<string> LoadAny(string regionId, string field)
        {
            if (field.Contains('.', StringComparison.Ordinal) || field == PathValue.ELEMENT)
                return Load(regionId, field);

            var result = Load(regionId, field);
            foreach (var key in _fields.Keys.Where(key => key.Region == regionId && FieldSlot.Name(key.Field) == field))
                result.UnionWith(Load(regionId, key.Field));
            return result;
        }

        /// <summary>A field of a region, joined with the same field of the open regions of its group, or of every region of the group
        /// when the region itself is open.</summary>
        private HashSet<string> Load(string regionId, string field)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (!_regions.TryGetValue(regionId, out var region))
                return result;
            foreach (var member in _groups[region.Group])
            {
                if (member == regionId || region.IsOpen || _regions[member].IsOpen)
                    result.UnionWith(_fields.GetValueOrDefault((member, field)) ?? []);
            }

            return result;
        }

        private string StaticTypeKey(InstanceState instance, IrFieldRef field) =>
            ProgramIndex.Substitute(DiIndex.TypeKey(field.Assembly, field.ContainingTypeId), instance.Substitution);

        private string StaticRegion(InstanceState instance, IrFieldRef field)
        {
            var typeKey = StaticTypeKey(instance, field);
            return Region($"static:{typeKey}", HeapRegionKind.Static, $"static:{DisplayType(typeKey)}", typeKey, "",
                          $"static|{_program.Decompose(typeKey).DefinitionKey}", merged: instance.IsMerged && _program.IsOpen(typeKey));
        }

        private string AllocationRegion(InstanceState instance, CreationSite site)
        {
            var typeKey = ProgramIndex.Substitute(site.TypeKey, instance.Substitution);
            if (_registeredAllocations.TryGetValue((site.BodyId, site.OperationId, ContextKey(instance)), out var registered) ||
                _factoryInstances.TryGetValue(instance.Id, out registered))
            {
                if (!_regionTypes.TryGetValue(registered, out var types))
                    _regionTypes.Add(registered, types = new HashSet<string>(StringComparer.Ordinal));
                Add(types, [typeKey]);
                return registered;
            }

            var owner = _scope.Reachable.Bodies.TryGetValue(site.BodyId, out var body) ? body.OwnerSymbol : site.BodyId;
            var ordinal = site.SiteOrdinal > 1 ? $"#{site.SiteOrdinal}" : "";
            return Region($"alloc|{site.BodyId}#{site.OperationId}|{typeKey}|{ContextKey(instance)}", HeapRegionKind.Allocation,
                          $"alloc:{owner}#{DisplayType(typeKey)}{ordinal}", typeKey, instance.Context, $"alloc|{site.BodyId}#{site.OperationId}",
                          merged: instance.IsMerged, site: site);
        }

        private string DelegateRegion(InstanceState instance, DelegateCreationValue created)
        {
            var site = created.Site;
            var identity = $"delegate|{site.BodyId}#{site.OperationId}|{ContextKey(instance)}";
            if (_delegates.ContainsKey(identity))
                return identity;

            var transfer = instance.Summary.Delegates.FirstOrDefault(candidate => candidate.OperationId == site.OperationId);
            var method = _program.Method(created.Target);
            var owner = _scope.Reachable.Bodies.TryGetValue(site.BodyId, out var body) ? body.OwnerSymbol : site.BodyId;
            var ordinal = site.SiteOrdinal > 1 ? $"#{site.SiteOrdinal}" : "";
            Region(identity, HeapRegionKind.Delegate, $"delegate:{owner}#{method?.DisplaySymbol ?? created.Target}{ordinal}",
                   SubstituteDisplay(site.TypeKey, instance.Substitution),
                   instance.Context, $"delegate|{site.BodyId}#{site.OperationId}", merged: instance.IsMerged, site: site);
            _delegates[identity] = new DelegateState(
                created.Target, method is null,
                transfer?.TargetContainingTypeKey is { } containing ? ProgramIndex.Substitute(containing, instance.Substitution) : null,
                (transfer?.TargetMethodTypeArgumentKeys ?? []).Select(key => ProgramIndex.Substitute(key, instance.Substitution)).ToArray(),
                instance.Substitution, transfer?.IsNonVirtual == true);
            return identity;
        }

        /// <summary>A delegate creation names its type as the IR value's display type, not as a type key, so its substitution is
        /// applied by display name: <c>Action&lt;T&gt;</c> created in an instance of <c>T = Order</c> is <c>Action&lt;Order&gt;</c>.</summary>
        private static string SubstituteDisplay(string type, IReadOnlyDictionary<string, string> substitution)
        {
            if (substitution.Count == 0)
                return type;
            var byDisplay = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (parameter, argument) in substitution)
                byDisplay.TryAdd(DisplayType(parameter), DisplayType(argument));
            return ProgramIndex.Substitute(type, byDisplay);
        }

        /// <summary>An instance's context with its substitution: one body reached from one call site with two type arguments runs in
        /// two contexts, so it creates two objects, even when the created type itself is not generic.</summary>
        private static string ContextKey(InstanceState instance) =>
            instance.Substitution.Count == 0
                ? instance.Context
                : $"{instance.Context}|{string.Join(",", instance.Substitution.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                                                             .Select(pair => $"{pair.Key}={pair.Value}"))}";

        private string Region(string identity, HeapRegionKind kind, string display, string? typeKey, string context, string? group,
                              bool merged = false, bool mayOverlapItself = false, CreationSite? site = null)
        {
            if (_regions.ContainsKey(identity))
                return identity;
            group ??= identity;
            _regions.Add(identity, new HeapRegion(identity, kind, display, typeKey, context, group,
                                                  typeKey is not null && _program.IsOpen(typeKey), merged, mayOverlapItself,
                                                  site?.BodyId, site?.OperationId));
            if (!_groups.TryGetValue(group, out var members))
                _groups.Add(group, members = []);
            members.Add(identity);
            _changes++;
            return identity;
        }

        private HashSet<string> CapturedKeys(string memberId)
        {
            if (_capturedKeysOfMember.TryGetValue(memberId, out var keys))
                return keys;
            keys = new HashSet<string>(StringComparer.Ordinal);
            var nested = _program.Method(memberId)?.NestedBodyIds ?? [];
            foreach (var bodyId in nested)
            {
                if (_scope.Reachable.Bodies.TryGetValue(bodyId, out var body))
                {
                    keys.UnionWith(body.Blocks.SelectMany(block => block.Operations).OfType<IrCaptureOperation>()
                                       .Select(capture => capture.SymbolKey).OfType<string>());
                }
            }

            _capturedKeysOfMember.Add(memberId, keys);
            return keys;
        }

        /// <summary>Counts a changing round of an instance; a call-graph cycle that keeps changing past the budget has its bodies'
        /// contexts merged, and propagation continues.</summary>
        private void CountRound(InstanceState instance)
        {
            var rounds = _changedRounds.GetValueOrDefault(instance.Id) + 1;
            _changedRounds[instance.Id] = rounds;
            if (rounds <= _limits.MaxSccIterations || _sccHandled.Contains(instance.Id))
                return;

            var component = Component(instance.Id);
            var cyclic = component.Count > 1 || _edges.Any(edge => edge.Caller == instance.Id && edge.Callee == instance.Id);
            _sccHandled.UnionWith(component);
            if (!cyclic)
                return;

            _mergedBodies.UnionWith(component.Select(id => _instances[id].BodyId));
            _counters[HeapCounters.SCC_BUDGET_EXCEEDED] = _counters.GetValueOrDefault(HeapCounters.SCC_BUDGET_EXCEEDED) + 1;
        }

        /// <summary>The strongly connected component of the instance call graph that contains <paramref name="start"/>: the instances
        /// it reaches that also reach it.</summary>
        private HashSet<string> Component(string start)
        {
            var successors = _edges.GroupBy(edge => edge.Caller, StringComparer.Ordinal)
                                   .ToDictionary(group => group.Key, group => group.Select(edge => edge.Callee).ToArray(), StringComparer.Ordinal);
            var predecessors = _edges.GroupBy(edge => edge.Callee, StringComparer.Ordinal)
                                     .ToDictionary(group => group.Key, group => group.Select(edge => edge.Caller).ToArray(), StringComparer.Ordinal);
            var forward = Reach(start, successors);
            var backward = Reach(start, predecessors);
            forward.IntersectWith(backward);
            forward.Add(start);
            return forward;
        }

        private static HashSet<string> Reach(string start, IReadOnlyDictionary<string, string[]> graph)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([start]);
            while (pending.TryPop(out var current))
            {
                foreach (var next in graph.GetValueOrDefault(current) ?? [])
                {
                    if (seen.Add(next))
                        pending.Push(next);
                }
            }

            return seen;
        }

        private HashSet<string> Field(string region, string field) => Get(_fields, (region, field));

        private HashSet<string> Cell(string owner, string key) => Get(_cells, (owner, key));

        private static HashSet<string> Parameter(InstanceState instance, int ordinal) => Get(instance.Parameters, ordinal);

        private static HashSet<string> CallResult(InstanceState instance, int operation) => Get(instance.CallResults, operation);

        /// <summary>Whether a value may come from an origin points-to does not follow: a parameter, a captured variable, an opaque call or
        /// an operation the summary does not model. A null or a field read before its first write is no object, and a source call is
        /// followed to its callee, so it counts only when that callee's own result is unfollowed.</summary>
        private static bool Unfollowed(InstanceState instance, IReadOnlySet<UnknownSource> sources, IReadOnlySet<int> calls) =>
            sources.Any(source => source is not (UnknownSource.Null or UnknownSource.FieldBeforeWrite or UnknownSource.SourceCall)) ||
            calls.Any(instance.UnfollowedCallResults.Contains);

        private static HashSet<string> RefResult(InstanceState instance, int operation, int ordinal) => Get(instance.RefResults, (operation, ordinal));

        private static HashSet<string> RefParameter(InstanceState instance, int ordinal) => Get(instance.RefParameters, ordinal);

        private static HashSet<string> Get<TKey>(Dictionary<TKey, HashSet<string>> map, TKey key) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var set))
                map.Add(key, set = new HashSet<string>(StringComparer.Ordinal));
            return set;
        }

        private void Add(HashSet<string> target, IEnumerable<string> values)
        {
            foreach (var value in values.ToArray())
            {
                if (target.Add(value))
                    _changes++;
            }
        }
    }
}
